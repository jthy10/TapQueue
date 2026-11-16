using Microsoft.Data.Sqlite;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <summary>What printing costs here and how it's accounted for. Kept in the settings table.</summary>
/// <param name="Mode">One of <see cref="AccountingMode"/>.</param>
/// <param name="Currency">How amounts are written: a symbol ("$") or letters ("kr").</param>
/// <param name="PriceMono">A page in black and white, in mills (<see cref="Money"/>), on a printer that doesn't set its own.</param>
/// <param name="PriceColor">A page in colour, likewise.</param>
/// <param name="DuplexDiscountPercent">How much less a page printed on both sides of the paper costs.</param>
/// <param name="InitialBalance">What a new user starts with.</param>
public sealed record AccountingSettings(string Mode = AccountingMode.Off, string Currency = "$", long PriceMono = 0, long PriceColor = 0,
    int DuplexDiscountPercent = 0, long InitialBalance = 0)
{
    public AccountingSettingsDto ToDto() => new(Mode, Currency, PriceMono, PriceColor, DuplexDiscountPercent, InitialBalance);
}

/// <summary>
/// Cost accounting. Each printer has a price per page, in black and white and in colour (its own, or
/// the server's default). A job's cost is worked out when it's released, from its pages and copies,
/// whether it prints in colour, and a discount for printing on both sides. What happens with that
/// cost is the mode:
/// <list type="bullet">
/// <item><see cref="AccountingMode.Off"/>: nothing; jobs have no cost.</item>
/// <item><see cref="AccountingMode.Track"/>: the cost is recorded with the job, for reports and charging departments back. Nobody is refused.</item>
/// <item><see cref="AccountingMode.Enforce"/>: the cost also comes off the person's balance, and a job they can't pay for stays held.</item>
/// </list>
/// Every change to a balance is a line in <c>balance_transactions</c>, with the balance it left. Pages
/// that didn't print (a job stopped at the printer, or one the printer gave up on) are given back in
/// money the same way they're given back to a page limit.
/// </summary>
public sealed class Accounts(Database database)
{
    private const string SettingsKey = "accounting";

    public AccountingSettings Settings() =>
        database.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", SettingsKey)) is string json
            ? System.Text.Json.JsonSerializer.Deserialize<AccountingSettings>(json, TapQueueJson.Options) ?? new AccountingSettings()
            : new AccountingSettings();

    public void Save(AccountingSettings settings)
    {
        database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
            ("$k", SettingsKey), ("$v", System.Text.Json.JsonSerializer.Serialize(settings, TapQueueJson.Options)));
        // UserStore gives new users this without knowing about accounting.
        database.Execute("INSERT INTO settings (key, value) VALUES ('initialBalance', $v) ON CONFLICT (key) DO UPDATE SET value = $v",
            ("$v", settings.InitialBalance.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public string Format(long mills) => Money.Format(mills, Settings().Currency);

    /// <summary>What a page costs at <paramref name="printer"/>: its own price if it has one, otherwise the server's.</summary>
    public (long Mono, long Color) PricesAt(PrinterRecord printer, AccountingSettings? settings = null)
    {
        settings ??= Settings();
        return (printer.PriceMono ?? settings.PriceMono, printer.PriceColor ?? settings.PriceColor);
    }

    /// <summary>
    /// What releasing a job at <paramref name="printer"/> costs, or null when costs aren't kept
    /// (<see cref="AccountingMode.Off"/>). <paramref name="color"/> and <paramref name="duplex"/> are how it will
    /// actually come out there.
    /// </summary>
    public long? CostOf(int chargedPages, bool color, bool duplex, PrinterRecord printer)
    {
        var settings = Settings();
        if (settings.Mode == AccountingMode.Off)
            return null;
        var (mono, colour) = PricesAt(printer, settings);
        var cost = chargedPages * (color ? colour : mono);
        return duplex ? cost * (100 - settings.DuplexDiscountPercent) / 100 : cost;
    }

    /// <summary>Why <paramref name="user"/> can't pay <paramref name="cost"/> now, or null if they can (or don't have to).</summary>
    public string? Refusal(UserRecord user, long? cost)
    {
        var settings = Settings();
        if (settings.Mode != AccountingMode.Enforce || cost is not > 0 || user.Unrestricted)
            return null;
        var balance = Balance(user.Id);
        return balance >= cost ? null
            : $"This job costs {Money.Format(cost.Value, settings.Currency)} and you have {Money.Format(balance, settings.Currency)}. Top up your balance, then release it again.";
    }

    public long Balance(long userId) => Convert.ToInt64(database.Scalar("SELECT balance FROM users WHERE id = $u", ("$u", userId)) ?? 0L);

    /// <summary>
    /// Records what a job cost as it's released, and when balances are kept takes it off its owner's.
    /// Returns their balance afterwards, or null if nothing came off one.
    /// </summary>
    public long? Charge(JobRecord job, long? cost, string printerName)
    {
        if (cost is not { } amount)
            return null;
        database.Execute("UPDATE jobs SET cost = $c, refunded_cost = 0 WHERE id = $id", ("$c", amount), ("$id", job.Id));
        if (Settings().Mode != AccountingMode.Enforce || job.UserId is not { } userId || amount == 0)
            return null;
        return Move(userId, -amount, TransactionKind.Charge, EventLog.System, $"\"{job.Name}\" at {printerName}", job.Id);
    }

    /// <summary>
    /// <paramref name="refundedPages"/> of a job's <paramref name="chargedPages"/> didn't print: gives that share
    /// of its cost back. Returns what was given back.
    /// </summary>
    public long RefundPages(JobRecord job, int refundedPages, int chargedPages)
    {
        if (job.Cost is not { } cost || chargedPages <= 0 || refundedPages <= 0)
            return 0;
        var refund = Math.Min(cost * Math.Min(refundedPages, chargedPages) / chargedPages, cost - job.RefundedCost);
        if (refund <= 0)
            return 0;
        database.Execute("UPDATE jobs SET refunded_cost = refunded_cost + $r WHERE id = $id", ("$r", refund), ("$id", job.Id));
        Credit(job, refund, refundedPages >= chargedPages ? "didn't print" : $"{refundedPages} of {chargedPages} pages didn't print");
        return refund;
    }

    /// <summary>
    /// A released job went back on hold (the printer gave up on it): it costs nothing after all, until
    /// it's released again. <paramref name="job"/> is the job as it was while released.
    /// </summary>
    public long RefundAll(JobRecord job)
    {
        if (job.Cost is not { } cost)
            return 0;
        var refund = cost - job.RefundedCost;
        database.Execute("UPDATE jobs SET cost = NULL, refunded_cost = 0 WHERE id = $id", ("$id", job.Id));
        if (refund > 0)
            Credit(job, refund, "the printer gave up on it");
        return Math.Max(0, refund);
    }

    /// <summary>Puts money back on the owner's balance, if the job's cost ever came off it.</summary>
    private void Credit(JobRecord job, long refund, string why)
    {
        if (job.UserId is not { } userId)
            return;
        var charged = database.Scalar("SELECT 1 FROM balance_transactions WHERE job_id = $j AND kind = $charge LIMIT 1",
            ("$j", job.Id), ("$charge", TransactionKind.Charge)) is not null;
        if (charged)
            Move(userId, refund, TransactionKind.Refund, EventLog.System, $"\"{job.Name}\": {why}", job.Id);
    }

    /// <summary>An admin adding to or taking from a balance. Returns the balance afterwards.</summary>
    public long Adjust(long userId, long amount, string actor, string note) =>
        Move(userId, amount, TransactionKind.Adjust, actor, note, null);

    /// <summary>An admin setting a balance to an amount. Returns the difference it made.</summary>
    public long SetTo(long userId, long balance, string actor, string note)
    {
        var difference = balance - Balance(userId);
        if (difference != 0)
            Move(userId, difference, TransactionKind.Adjust, actor, note, null);
        return difference;
    }

    public void SetUnrestricted(long userId, bool unrestricted) =>
        database.Execute("UPDATE users SET unrestricted = $v WHERE id = $u", ("$v", unrestricted), ("$u", userId));

    /// <summary>Newest first.</summary>
    public List<BalanceTransactionDto> Transactions(long userId, int limit = 100) =>
        database.Query("""
            SELECT id, at, kind, amount, balance_after, job_id, actor, note FROM balance_transactions
            WHERE user_id = $u ORDER BY id DESC LIMIT $limit
            """, r => new BalanceTransactionDto(r.GetInt64(0), r.GetTime(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64OrNull(5), r.GetString(6), r.GetString(7)),
            ("$u", userId), ("$limit", limit));

    /// <summary>Changes a balance and writes the line that says why, together.</summary>
    private long Move(long userId, long amount, string kind, string actor, string note, long? jobId)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        long after;
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE users SET balance = balance + $a WHERE id = $u RETURNING balance";
            update.Parameters.AddWithValue("$a", amount);
            update.Parameters.AddWithValue("$u", userId);
            after = Convert.ToInt64(update.ExecuteScalar() ?? throw new InvalidOperationException("No such user."));
        }
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO balance_transactions (user_id, at, kind, amount, balance_after, job_id, actor, note)
                VALUES ($u, $at, $kind, $a, $after, $job, $actor, $note)
                """;
            insert.Parameters.AddWithValue("$u", userId);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$a", amount);
            insert.Parameters.AddWithValue("$after", after);
            insert.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$actor", actor);
            insert.Parameters.AddWithValue("$note", note.Length <= 200 ? note : note[..200]);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return after;
    }
}
