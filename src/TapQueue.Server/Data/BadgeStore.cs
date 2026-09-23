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
        var id = database.Scalar("UPDATE badges SET last_used_at = $now WHERE card_hash = $h RETURNING id",
            ("$now", DateTimeOffset.UtcNow), ("$h", Tokens.Hash(normalized)));
        return id is long badgeId ? Get(badgeId) : null;
    }

    public BadgeRecord? FindByCard(string card) =>
        database.QueryOne(SelectColumns + " WHERE b.card_hash = $h", Map, ("$h", Tokens.Hash(Normalize(card))));

    public BadgeRecord? Get(long id) =>
        database.QueryOne(SelectColumns + " WHERE b.id = $id", Map, ("$id", id));

    public List<BadgeRecord> List(long? userId = null) =>
        database.Query(SelectColumns + (userId is null ? "" : " WHERE b.user_id = $u") + " ORDER BY u.username, b.id", Map, ("$u", userId));

    /// <summary>Links a card to a user. The caller checks that the card isn't already someone's.</summary>
    public BadgeRecord Add(long userId, string card)
    {
        var normalized = Normalize(card);
        var id = (long)database.Scalar("""
            INSERT INTO badges (card_hash, card_hint, user_id, created_at)
            VALUES ($h, $hint, $u, $now)
            RETURNING id
            """, ("$h", Tokens.Hash(normalized)), ("$hint", Hint(normalized)), ("$u", userId), ("$now", DateTimeOffset.UtcNow))!;
        return Get(id)!;
    }

    public bool Delete(long id) =>
        database.Execute("DELETE FROM badges WHERE id = $id", ("$id", id)) == 1;

    private static BadgeRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetTime(4), r.GetTimeOrNull(5));
}
