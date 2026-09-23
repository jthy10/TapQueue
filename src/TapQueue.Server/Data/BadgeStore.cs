using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record BadgeRecord(long Id, long UserId, string Username, string CardHint, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public BadgeDto ToDto() => new(Id, Username, CardHint, CreatedAt, LastUsedAt);
}

/// <summary>
/// Badges link a card number (whatever the reader types) to a user. Card numbers are stored hashed,
/// with only the last few characters kept so an admin can tell badges apart.
/// </summary>
public sealed class BadgeStore(Database database)
{
    private const string SelectColumns = """
        SELECT b.id, b.user_id, u.username, b.card_hint, b.created_at, b.last_used_at
        FROM badges b JOIN users u ON u.id = b.user_id
        """;

    /// <summary>Readers differ in case and padding; "04a1b2 " and "04A1B2" are the same card.</summary>
    public static string Normalize(string card) =>
        new string(card.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToUpperInvariant();

    public static string Hint(string normalizedCard) =>
        normalizedCard.Length <= 4 ? normalizedCard : "…" + normalizedCard[^4..];

    /// <summary>Finds the badge for a card and records that it was just used.</summary>
    public BadgeRecord? Use(string card)
    {
        var normalized = Normalize(card);
        if (normalized.Length == 0) return null;
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE badges SET last_used_at = $now WHERE card_hash = $h RETURNING id";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(normalized));
        return cmd.ExecuteScalar() is long id ? Get(id) : null;
    }

    public BadgeRecord? FindByCard(string card)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE b.card_hash = $h";
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(Normalize(card)));
        return ReadAll(cmd).FirstOrDefault();
    }

    public BadgeRecord? Get(long id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE b.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public List<BadgeRecord> List(long? userId = null)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + (userId is null ? "" : " WHERE b.user_id = $u") + " ORDER BY u.username, b.id";
        if (userId is not null)
            cmd.Parameters.AddWithValue("$u", userId);
        return ReadAll(cmd);
    }

    /// <summary>Links a card to a user. The caller checks that the card isn't already someone's.</summary>
    public BadgeRecord Add(long userId, string card)
    {
        var normalized = Normalize(card);
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO badges (card_hash, card_hint, user_id, created_at)
            VALUES ($h, $hint, $u, $now)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$h", Tokens.Hash(normalized));
        cmd.Parameters.AddWithValue("$hint", Hint(normalized));
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Get((long)cmd.ExecuteScalar()!)!;
    }

    public bool Delete(long id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM badges WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    private static List<BadgeRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var badges = new List<BadgeRecord>();
        while (r.Read())
        {
            badges.Add(new BadgeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4)), r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5))));
        }
        return badges;
    }
}
