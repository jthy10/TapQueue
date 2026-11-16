using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>
/// People linking their own badge. Someone signed in (the tray app, the web page) says which printer
/// they're standing at; for the next minute, a card nobody owns that's tapped at a station there
/// becomes theirs. Nobody types a card number and no admin is involved.
///
/// One person at a time per station: while someone is waiting there, anyone else who asks is told to
/// try again in a moment, so a card can't go to the wrong one of two people. Kept in memory: a
/// restart ends whatever was waiting, and people just ask again.
/// </summary>
public sealed class BadgeEnrollments
{
    /// <summary>How long after asking a tap counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>How long "linked" or "nothing was tapped" stays there to be read, by a tray app polling for it.</summary>
    private static readonly TimeSpan ResultKeptFor = TimeSpan.FromMinutes(5);

    /// <summary>Cards one person may link by themselves. An admin can link more.</summary>
    public const int MaxCardsPerPerson = 5;

    private sealed record Entry(long UserId, IReadOnlyList<string> StationIds, string PrinterName, DateTimeOffset ExpiresAt, string? LinkedHint = null, DateTimeOffset? LinkedAt = null);

    private readonly Dictionary<long, Entry> _byUser = [];

    /// <summary>
    /// Starts waiting for <paramref name="userId"/>'s card at <paramref name="stationIds"/>. Returns
    /// null if someone else is waiting at one of them already.
    /// </summary>
    public BadgeEnrollmentDto? Start(long userId, IReadOnlyList<string> stationIds, string printerName)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_byUser)
        {
            Sweep(now);
            if (_byUser.Values.Any(e => e.UserId != userId && Waiting(e, now) && e.StationIds.Intersect(stationIds, StringComparer.OrdinalIgnoreCase).Any()))
                return null;
            var entry = new Entry(userId, stationIds, printerName, now + Window);
            _byUser[userId] = entry;
            return Describe(entry, now);
        }
    }

    /// <summary>Where <paramref name="userId"/>'s own enrollment stands.</summary>
    public BadgeEnrollmentDto Status(long userId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_byUser)
            return _byUser.TryGetValue(userId, out var entry) && now - (entry.LinkedAt ?? entry.ExpiresAt) < ResultKeptFor
                ? Describe(entry, now)
                : new BadgeEnrollmentDto(BadgeEnrollmentState.None, "");
    }

    public void Cancel(long userId)
    {
        lock (_byUser)
            if (_byUser.TryGetValue(userId, out var entry) && entry.LinkedHint is null)
                _byUser.Remove(userId);
    }

    /// <summary>
    /// A card nobody owns was tapped at <paramref name="stationId"/>: the person waiting for theirs
    /// there, or null. They stop waiting; <see cref="Linked"/> then records how it went.
    /// </summary>
    public long? Claim(string stationId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_byUser)
        {
            var entry = _byUser.Values.FirstOrDefault(e => Waiting(e, now) && e.StationIds.Contains(stationId, StringComparer.OrdinalIgnoreCase));
            if (entry is null)
                return null;
            _byUser[entry.UserId] = entry with { ExpiresAt = now };
            return entry.UserId;
        }
    }

    /// <summary>The card tapped for <paramref name="userId"/> is theirs now.</summary>
    public void Linked(long userId, string cardHint)
    {
        lock (_byUser)
            if (_byUser.TryGetValue(userId, out var entry))
                _byUser[userId] = entry with { LinkedHint = cardHint, LinkedAt = DateTimeOffset.UtcNow };
    }

    private static bool Waiting(Entry entry, DateTimeOffset now) => entry.LinkedHint is null && entry.ExpiresAt > now;

    private static BadgeEnrollmentDto Describe(Entry entry, DateTimeOffset now) =>
        entry.LinkedHint is { } hint
            ? new BadgeEnrollmentDto(BadgeEnrollmentState.Linked, $"Card {hint} is now linked to your account. Tap it at any TapQueue printer to print.", 0, hint, entry.PrinterName)
        : Waiting(entry, now)
            ? new BadgeEnrollmentDto(BadgeEnrollmentState.Waiting, $"Tap your badge on the reader at {entry.PrinterName} now.",
                (int)Math.Ceiling((entry.ExpiresAt - now).TotalSeconds), null, entry.PrinterName)
        : new BadgeEnrollmentDto(BadgeEnrollmentState.Expired,
            $"No new card was tapped at {entry.PrinterName} in time. If your badge is already linked to someone, ask an admin.", 0, null, entry.PrinterName);

    private void Sweep(DateTimeOffset now)
    {
        foreach (var gone in _byUser.Values.Where(e => now - (e.LinkedAt ?? e.ExpiresAt) >= ResultKeptFor).Select(e => e.UserId).ToList())
            _byUser.Remove(gone);
    }
}
