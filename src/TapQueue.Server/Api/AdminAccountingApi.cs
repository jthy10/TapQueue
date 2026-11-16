using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin: what printing costs (<c>/accounting</c>) and people's balances (<c>/accounts</c>,
/// <c>/users/{username}/account</c>). See <see cref="Accounts"/>. Amounts are in mills.
/// </summary>
public static class AdminAccountingApi
{
    public static void MapAccountingApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/accounting", (Accounts accounts) => accounts.Settings().ToDto());
        admin.MapPatch("/accounting", Update);
        // Everyone's balance at once, for the Balances page.
        admin.MapGet("/accounts", (UserStore users) => users.List().Select(u => new UserAccountDto(u.Username, u.DisplayName, u.Balance, u.Unrestricted, [])));
        admin.MapGet("/users/{username}/account", (string username, UserStore users, Accounts accounts) =>
            users.FindByUsername(username) is { } user ? Results.Ok(Account(user, accounts)) : NoUser(username));
        admin.MapPost("/users/{username}/balance", Adjust);
        admin.MapPut("/users/{username}/unrestricted", SetUnrestricted);
        admin.MapPost("/groups/{id}/balance", AdjustGroup);
    }

    private static IResult NoUser(string username) => Results.NotFound(new ErrorResponse($"No user \"{username}\"."));

    private static UserAccountDto Account(UserRecord user, Accounts accounts) =>
        new(user.Username, user.DisplayName, accounts.Balance(user.Id), user.Unrestricted, accounts.Transactions(user.Id));

    /// <summary>Why a price isn't one, or null. <paramref name="allowDefault"/>: -1 means "the server's price".</summary>
    public static string? PriceProblem(long? price, bool allowDefault) =>
        price is null || price is >= 0 and <= Money.Max || allowDefault && price == -1 ? null
        : $"A price is 0 to {Money.Max / Money.PerUnit} a page, in thousandths{(allowDefault ? ", or -1 for the server's price" : "")}.";

    private static IResult Update(UpdateAccountingRequest request, Accounts accounts, EventLog events)
    {
        if (request.Mode is { } mode && !AccountingMode.All.Contains(mode))
            return Results.BadRequest(new ErrorResponse($"mode must be one of: {string.Join(", ", AccountingMode.All)}."));
        var currency = request.Currency?.Trim();
        if (currency is { Length: 0 or > 5 } || currency?.Any(c => char.IsControl(c) || char.IsDigit(c) || char.IsWhiteSpace(c)) == true)
            return Results.BadRequest(new ErrorResponse("currency is a symbol or a few letters, like $, € or kr."));
        if ((PriceProblem(request.PriceMono, false) ?? PriceProblem(request.PriceColor, false)) is { } price)
            return Results.BadRequest(new ErrorResponse(price));
        if (request.DuplexDiscountPercent is < 0 or > 100)
            return Results.BadRequest(new ErrorResponse("duplexDiscountPercent must be 0 to 100."));
        if (request.InitialBalance is < 0 or > Money.Max)
            return Results.BadRequest(new ErrorResponse($"initialBalance must be 0 to {Money.Max / Money.PerUnit}."));

        var old = accounts.Settings();
        var settings = old with
        {
            Mode = request.Mode ?? old.Mode,
            Currency = currency ?? old.Currency,
            PriceMono = request.PriceMono ?? old.PriceMono,
            PriceColor = request.PriceColor ?? old.PriceColor,
            DuplexDiscountPercent = request.DuplexDiscountPercent ?? old.DuplexDiscountPercent,
            InitialBalance = request.InitialBalance ?? old.InitialBalance,
        };
        if (settings == old)
            return Results.Ok(settings.ToDto());
        accounts.Save(settings);

        string M(long mills) => Money.Format(mills, settings.Currency);
        var changes = new List<string>();
        if (settings.Mode != old.Mode)
            changes.Add(settings.Mode switch
            {
                AccountingMode.Enforce => "people pay for printing from their balance",
                AccountingMode.Track => "each job's cost is recorded, and nobody is refused",
                _ => "printing costs nothing",
            });
        if (settings.Currency != old.Currency) changes.Add($"amounts are in {settings.Currency}");
        if (settings.PriceMono != old.PriceMono) changes.Add($"a black and white page costs {M(settings.PriceMono)}");
        if (settings.PriceColor != old.PriceColor) changes.Add($"a colour page costs {M(settings.PriceColor)}");
        if (settings.DuplexDiscountPercent != old.DuplexDiscountPercent) changes.Add($"two-sided pages cost {settings.DuplexDiscountPercent}% less");
        if (settings.InitialBalance != old.InitialBalance) changes.Add($"new users start with {M(settings.InitialBalance)}");
        events.Admin(null, $"Cost accounting: {string.Join("; ", changes)}.");
        return Results.Ok(settings.ToDto());
    }

    private static IResult Adjust(string username, AdjustBalanceRequest request, UserStore users, Accounts accounts, EventLog events)
    {
        if (users.FindByUsername(username) is not { } user)
            return NoUser(username);
        if (Math.Abs(request.Amount) > Money.Max || !request.Set && request.Amount == 0)
            return Results.BadRequest(new ErrorResponse($"amount is in thousandths, up to {Money.Max / Money.PerUnit} either way, and not 0."));
        var note = Note(request.Note);

        var difference = request.Set
            ? accounts.SetTo(user.Id, request.Amount, EventLog.CurrentAdmin, note)
            : request.Amount;
        if (!request.Set)
            accounts.Adjust(user.Id, request.Amount, EventLog.CurrentAdmin, note);
        if (difference != 0)
            events.Admin(EventLog.User(user.Username),
                $"{(difference > 0 ? "Added" : "Took")} {accounts.Format(Math.Abs(difference))} {(difference > 0 ? "to" : "from")} {user.Username}'s balance" +
                $"{(note.Length > 0 ? $" ({note})" : "")}; it's now {accounts.Format(accounts.Balance(user.Id))}.");
        return Results.Ok(Account(users.FindById(user.Id)!, accounts));
    }

    private static IResult SetUnrestricted(string username, SetUnrestrictedRequest request, UserStore users, Accounts accounts, EventLog events)
    {
        if (users.FindByUsername(username) is not { } user)
            return NoUser(username);
        if (user.Unrestricted != request.Unrestricted)
        {
            accounts.SetUnrestricted(user.Id, request.Unrestricted);
            events.Admin(EventLog.User(user.Username), request.Unrestricted
                ? $"{user.Username} may print with nothing on their balance."
                : $"{user.Username} can only print what their balance covers.");
        }
        return Results.Ok(Account(users.FindById(user.Id)!, accounts));
    }

    /// <summary>Adds to (or takes from) the balance of everyone in a group who isn't disabled: a term's allowance, say.</summary>
    private static IResult AdjustGroup(string id, AdjustBalanceRequest request, GroupStore groups, UserStore users, Accounts accounts, EventLog events)
    {
        if (groups.Get(id) is not { } group)
            return Results.NotFound(new ErrorResponse($"No group \"{id}\"."));
        if (request.Set)
            return Results.BadRequest(new ErrorResponse("A group's members each get an amount added or taken away; set isn't for groups."));
        if (request.Amount == 0 || Math.Abs(request.Amount) > Money.Max)
            return Results.BadRequest(new ErrorResponse($"amount is in thousandths, up to {Money.Max / Money.PerUnit} either way, and not 0."));
        var note = Note(request.Note) is { Length: > 0 } given ? given : $"group {group.Name}";

        var memberships = groups.Memberships();
        var members = users.List().Where(u => !u.Disabled && (memberships.GetValueOrDefault(u.Id)?.Contains(group.Id, StringComparer.OrdinalIgnoreCase) ?? false)).ToList();
        foreach (var member in members)
            accounts.Adjust(member.Id, request.Amount, EventLog.CurrentAdmin, note);
        events.Admin(EventLog.Group(group.Id),
            $"{(request.Amount > 0 ? "Added" : "Took")} {accounts.Format(Math.Abs(request.Amount))} {(request.Amount > 0 ? "to" : "from")} the balance of each of the " +
            $"{members.Count} {(members.Count == 1 ? "person" : "people")} in group \"{group.Name}\".");
        return Results.Ok(new GroupBalanceResponse(members.Count));
    }

    private static string Note(string? note) => string.IsNullOrWhiteSpace(note) ? "" : note.Trim() is { Length: > 200 } text ? text[..200] : note.Trim();
}
