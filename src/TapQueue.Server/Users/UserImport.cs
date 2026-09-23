using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Users;

/// <summary>
/// CSV import and export of users. An import is planned first (what each row would change, or why it
/// can't) and only applied when asked, so the console can show a preview. Rows with errors are
/// skipped; the rest are applied.
///
/// Columns (header row required, any order, only username is required):
///   username       who the row is about; created if they don't exist
///   display_name   (or name) their display name
///   groups         group ids separated by ";". Replaces their groups; empty means none
///   card           a card number to link to them
///   disabled       true/false
/// card_hints and created_at (from an export) are ignored, so an export can be edited and imported back.
/// </summary>
public sealed class UserImport(UserStore users, GroupStore groups, BadgeStore badges, UnknownTaps unknownTaps, UserLifecycle lifecycle, EventLog events)
{
    private static readonly string[] Ignored = ["card_hints", "created_at"];

    private sealed record Planned(ImportRowDto Row, Action? Apply);

    public ImportResponse Run(string csv, bool apply)
    {
        var rows = Csv.Parse(csv);
        if (rows.Count == 0)
            throw new InvalidDataException("The file is empty.");

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToArray();
        var column = new Dictionary<string, int>();
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i] == "name" ? "display_name" : header[i];
            if (name is "username" or "display_name" or "groups" or "card" or "disabled")
                column[name] = i;
            else if (!Ignored.Contains(name) && name.Length > 0)
                throw new InvalidDataException($"Unknown column \"{rows[0][i]}\". Use username, display_name, groups, card and disabled.");
        }
        if (!column.ContainsKey("username"))
            throw new InvalidDataException("The first row must be a header with a username column.");

        var allGroups = groups.List().ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var memberships = groups.Memberships();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cardsInFile = new HashSet<string>();
        var planned = new List<Planned>();
        for (var r = 1; r < rows.Count; r++)
        {
            string? Cell(string name) => column.TryGetValue(name, out var i) ? (i < rows[r].Length ? rows[r][i].Trim() : "") : null;
            planned.Add(PlanRow(r + 1, Cell, seen, cardsInFile, allGroups, memberships));
        }

        var result = planned.Select(p => p.Row).ToList();
        if (apply)
        {
            for (var i = 0; i < planned.Count; i++)
            {
                try
                {
                    planned[i].Apply?.Invoke();
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
                {
                    result[i] = result[i] with { Action = ImportAction.Error, Error = ex.Message };
                }
            }
            events.Admin(null, $"Imported users from CSV: {result.Count(r => r.Action is ImportAction.Create or ImportAction.Update)} rows changed, {result.Count(r => r.Action == ImportAction.Error)} skipped.");
        }
        return new ImportResponse(apply,
            result.Count(r => r.Action == ImportAction.Create),
            result.Count(r => r.Action == ImportAction.Update),
            result.Count(r => r.Action == ImportAction.Error),
            result);
    }

    private Planned PlanRow(int rowNumber, Func<string, string?> cell, HashSet<string> seen, HashSet<string> cardsInFile,
        Dictionary<string, GroupRecord> allGroups, Dictionary<long, List<string>> memberships)
    {
        var username = cell("username") ?? "";
        Planned Error(string message) => new(new ImportRowDto(rowNumber, username, ImportAction.Error, [], message), null);

        if (username.Length == 0) return Error("No username.");
        if (!seen.Add(username)) return Error($"{username} is already in an earlier row.");

        var existing = users.FindByUsername(username);
        var changes = new List<string>();
        var steps = new List<Func<UserRecord?, UserRecord>>();

        var displayName = cell("display_name");
        if (existing is null)
        {
            changes.Add($"create{(string.IsNullOrEmpty(displayName) ? "" : $" as {displayName}")}");
            steps.Add(_ => lifecycle.Create(username, displayName).User);
        }
        else if (!string.IsNullOrEmpty(displayName) && displayName != existing.DisplayName)
        {
            changes.Add($"rename to {displayName}");
            steps.Add(u => lifecycle.Rename(u!, displayName));
        }

        if (cell("disabled") is { Length: > 0 } disabledText)
        {
            if (ParseBool(disabledText) is not { } disabled)
                return Error($"disabled must be true or false, not \"{disabledText}\".");
            if (disabled != (existing?.Disabled ?? false))
            {
                changes.Add(disabled ? "disable" : "enable");
                steps.Add(u => lifecycle.SetDisabled(u!, disabled));
            }
        }

        if (cell("groups") is { } groupsText)
        {
            var wanted = groupsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (wanted.FirstOrDefault(g => !allGroups.ContainsKey(g)) is { } unknown)
                return Error($"No group \"{unknown}\".");
            var current = existing is null ? [] : memberships.GetValueOrDefault(existing.Id) ?? [];
            var add = wanted.Where(g => !current.Contains(g, StringComparer.OrdinalIgnoreCase)).Select(g => allGroups[g]).ToList();
            var remove = current.Where(g => !wanted.Contains(g, StringComparer.OrdinalIgnoreCase)).Select(g => allGroups[g]).ToList();
            changes.AddRange(add.Select(g => $"add to {g.Name}"));
            changes.AddRange(remove.Select(g => $"remove from {g.Name}"));
            steps.Add(u =>
            {
                add.ForEach(g => lifecycle.AddToGroup(u!, g));
                remove.ForEach(g => lifecycle.RemoveFromGroup(u!, g));
                return u!;
            });
        }

        if (cell("card") is { Length: > 0 } cardText)
        {
            var card = BadgeStore.Normalize(cardText);
            if (!cardsInFile.Add(card))
                return Error($"Card {BadgeStore.Hint(card)} is already in an earlier row.");
            var owner = badges.FindByCard(card);
            if (owner is not null && !string.Equals(owner.Username, username, StringComparison.OrdinalIgnoreCase))
                return Error($"Card {BadgeStore.Hint(card)} already belongs to {owner.Username}.");
            if (owner is null)
            {
                changes.Add($"link card {BadgeStore.Hint(card)}");
                steps.Add(u =>
                {
                    var badge = badges.Add(u!.Id, card);
                    unknownTaps.Remove(card);
                    events.Admin(EventLog.User(u.Username), $"Linked card {badge.CardHint} to {u.Username}.");
                    return u;
                });
            }
        }

        var action = existing is null ? ImportAction.Create : changes.Count > 0 ? ImportAction.Update : ImportAction.Unchanged;
        return new(new ImportRowDto(rowNumber, username, action, changes, null), () =>
        {
            var user = existing;
            foreach (var step in steps)
                user = step(user);
        });
    }

    private static bool? ParseBool(string text) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" or "disabled" => true,
        "false" or "no" or "n" or "0" or "enabled" => false,
        _ => null,
    };

    public string Export()
    {
        var memberships = groups.Memberships();
        var cards = badges.List().GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.ToList());
        return Csv.Write(
            new[] { new[] { "username", "display_name", "groups", "disabled", "card_hints", "created_at" } }.Concat(
                users.List().Select(u => new[]
                {
                    u.Username,
                    u.DisplayName,
                    string.Join(';', memberships.GetValueOrDefault(u.Id) ?? []),
                    u.Disabled ? "true" : "false",
                    string.Join(';', (cards.GetValueOrDefault(u.Id) ?? []).Select(b => b.Label.Length > 0 ? $"{b.CardHint} ({b.Label})" : b.CardHint)),
                    u.CreatedAt.ToString("yyyy-MM-dd"),
                })));
    }
}
