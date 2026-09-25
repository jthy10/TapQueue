using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// Brings TapQueue's users and groups in line with Active Directory. A sync reads the scope
/// (<see cref="DirectoryReader"/>), works out every change first, and applies them only when it
/// isn't a preview, so the console can show exactly what a sync would do.
///
/// Users are matched by objectGUID, so renames and moves in AD keep their cards, jobs and limits. On
/// the first sync a local user with the same username is linked rather than duplicated. AD users
/// who are disabled or expired in AD, or have left the scope, are disabled here, and re-enabled when
/// that's no longer so, unless an admin disabled them. Nobody is deleted: their held jobs and
/// history stay, and an admin can delete users who left the scope.
///
/// Groups in the scope become TapQueue groups with everyone in them, through nested groups too. What
/// they may use and their page limits are set in TapQueue and kept across syncs.
///
/// If the badge attribute is set, AD owns the cards of users who have a value in it: that card is
/// theirs (moved from whoever had it) and their other cards are removed.
///
/// A sync that would disable more than MaxDisablePercent of the enabled AD users (and at least
/// <see cref="MinDisablesToStop"/>) stops without changing anything, in case AD or the scope is wrong;
/// forcing it goes ahead.
/// </summary>
public sealed class DirectorySync(
    DirectoryStore store,
    IDirectorySourceFactory sources,
    UserStore users,
    GroupStore groups,
    BadgeStore badges,
    EventLog events,
    ILogger<DirectorySync> logger)
{
    public const string ScheduleTrigger = "schedule";
    public const string AdminTrigger = "admin";

    /// <summary>A handful of leavers never stops a sync, whatever the percentage.</summary>
    public const int MinDisablesToStop = 5;

    private readonly SemaphoreSlim _running = new(1, 1);

    public bool Running => _running.CurrentCount == 0;

    private sealed record Change(DirectoryChangeDto Dto, Action? Apply, bool DisablesEnabledUser = false);

    /// <summary>Runs a sync, or a preview with <paramref name="dryRun"/>. Throws InvalidOperationException if one is already running.</summary>
    public DirectorySyncResultDto Run(string trigger, bool dryRun, bool force)
    {
        if (!_running.Wait(0))
            throw new InvalidOperationException("A directory sync is already running.");
        try
        {
            return RunLocked(trigger, dryRun, force);
        }
        finally
        {
            _running.Release();
        }
    }

    private DirectorySyncResultDto RunLocked(string trigger, bool dryRun, bool force)
    {
        var runId = store.StartRun(trigger, dryRun);
        var actor = trigger == ScheduleTrigger ? EventLog.System : EventLog.CurrentAdmin;
        var config = store.Config();
        var scope = store.Scope();

        DirectorySnapshot snapshot;
        try
        {
            if (scope.Count == 0)
                throw new DirectoryException("Nothing is in the scope yet: add the OUs, groups or users to sync.");
            using var source = sources.Connect(config);
            snapshot = new DirectoryReader(source).Read(scope, DateTimeOffset.UtcNow);
        }
        catch (DirectoryException ex)
        {
            return Fail(runId, dryRun, actor, ex.Message);
        }

        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>(snapshot.Warnings);
        var changes = PlanUsers(snapshot, config, ids, warnings);
        changes.AddRange(PlanGroups(snapshot, ids));

        var summary = Summarize(changes, warnings);
        var disabling = changes.Count(c => c.DisablesEnabledUser);
        var enabledAdUsers = users.List().Count(u => u.FromDirectory && !u.Disabled);
        if (!force && disabling >= MinDisablesToStop && disabling * 100 > enabledAdUsers * config.MaxDisablePercent)
        {
            var error = $"Stopped: this would disable {disabling} of {enabledAdUsers} Active Directory users, more than the {config.MaxDisablePercent}% allowed. " +
                "Check the scope and AD, then force the sync if it's right.";
            store.FinishRun(runId, DirectoryRunOutcome.Stopped, summary, error);
            logger.LogWarning("Directory sync stopped: it would disable {Count} of {Total} AD users", disabling, enabledAdUsers);
            events.Record(EventCategory.Directory, actor, null, error);
            return Result(runId, false, DirectoryRunOutcome.Stopped, summary, error, snapshot, changes, warnings);
        }

        if (!dryRun)
        {
            foreach (var change in changes)
            {
                try
                {
                    change.Apply?.Invoke();
                    events.Record(EventCategory.Directory, actor, Subject(change.Dto), change.Dto.Description);
                }
                // A step whose user wasn't created (see the warnings) has no id to work with.
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or KeyNotFoundException)
                {
                    warnings.Add($"Failed: {change.Dto.Description} ({ex.Message})");
                }
            }
            foreach (var (item, entry) in snapshot.Scope)
                if (item.Dn != entry.Dn || item.Name != entry.Name)
                    store.UpdateScope(item.Id, entry.Dn, entry.Name);
            summary = Summarize(changes, warnings);
            logger.LogInformation("Directory sync: {Summary}", summary);
            events.Record(EventCategory.Directory, actor, null, $"Active Directory sync: {summary}");
        }
        store.FinishRun(runId, DirectoryRunOutcome.Succeeded, summary, null);
        return Result(runId, !dryRun, DirectoryRunOutcome.Succeeded, summary, null, snapshot, changes, warnings);
    }

    private List<Change> PlanUsers(DirectorySnapshot snapshot, DirectoryConfig config, Dictionary<string, long> ids, List<string> warnings)
    {
        var changes = new List<Change>();
        var all = users.List();
        var byGuid = all.Where(u => u.FromDirectory && u.ExternalId is not null).ToDictionary(u => u.ExternalId!, StringComparer.OrdinalIgnoreCase);
        var byName = all.ToDictionary(u => u.Username, StringComparer.OrdinalIgnoreCase);
        var inScope = snapshot.Users.Select(u => u.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var syncBadges = !string.IsNullOrWhiteSpace(config.BadgeAttribute);
        var allBadges = syncBadges ? badges.List() : [];
        var claimedCards = new Dictionary<string, string>();

        foreach (var d in snapshot.Users)
        {
            var existing = byGuid.GetValueOrDefault(d.Guid);
            if (existing is null && byName.TryGetValue(d.Username, out var sameName))
            {
                if (sameName.FromDirectory)
                {
                    warnings.Add(inScope.Contains(sameName.ExternalId ?? "")
                        ? $"Skipped {d.Username}: another AD account in the scope has that username in TapQueue; the next sync sorts it out once that one's rename is done."
                        : $"Skipped {d.Username}: TapQueue's {d.Username} is a different AD account (deleted or out of the scope). Delete that user in TapQueue to let this one in.");
                    continue;
                }
                existing = sameName;
                var linked = sameName;
                changes.Add(new Change(new(DirectoryChangeAction.LinkUser, d.Username, $"Linked local user {d.Username} to Active Directory; they keep their cards and history."),
                    () => users.SetSource(linked.Id, UserSource.ActiveDirectory, d.Guid)));
            }

            var wantState = d.Disabled ? DirectoryState.Disabled : d.Expired ? DirectoryState.Expired : null;
            if (existing is null)
            {
                changes.Add(new Change(new(DirectoryChangeAction.CreateUser, d.Username,
                        $"Added {d.Username} ({d.DisplayName}) from Active Directory" + (wantState is null ? "." : $", disabled: {Why(wantState)}.")),
                    () =>
                    {
                        var created = users.Create(d.Username, d.DisplayName, Tokens.Hash(Tokens.New()));
                        users.SetSource(created.Id, UserSource.ActiveDirectory, d.Guid);
                        if (wantState is not null)
                            users.SetDisabled(created.Id, true, DisabledBy.Directory, wantState);
                        ids[d.Guid] = created.Id;
                    }));
                byName[d.Username] = new UserRecord(0, d.Username, d.DisplayName, null, default, null, UserSource.ActiveDirectory, ExternalId: d.Guid);
            }
            else
            {
                ids[d.Guid] = existing.Id;
                PlanUserUpdate(existing, d, wantState, byName, changes, warnings);
            }

            if (syncBadges)
                PlanBadge(existing, d, allBadges, claimedCards, ids, changes, warnings);
        }

        foreach (var gone in byGuid.Values.Where(u => !inScope.Contains(u.ExternalId!)))
        {
            if (!gone.Disabled)
                changes.Add(new Change(new(DirectoryChangeAction.DisableUser, gone.Username,
                        $"Disabled {gone.Username}: {Why(DirectoryState.Missing)}. Their held jobs are kept."),
                    () => users.SetDisabled(gone.Id, true, DisabledBy.Directory, DirectoryState.Missing), DisablesEnabledUser: true));
            else if (gone.DisabledBy == DisabledBy.Directory && gone.DirectoryState != DirectoryState.Missing)
                changes.Add(new Change(new(DirectoryChangeAction.UpdateUser, gone.Username, $"{gone.Username} is still disabled: {Why(DirectoryState.Missing)}."),
                    () => users.SetDirectoryState(gone.Id, DirectoryState.Missing)));
        }
        return changes;
    }

    private void PlanUserUpdate(UserRecord existing, DirectoryUser d, string? wantState, Dictionary<string, UserRecord> byName, List<Change> changes, List<string> warnings)
    {
        if (!string.Equals(existing.Username, d.Username, StringComparison.Ordinal))
        {
            if (byName.TryGetValue(d.Username, out var taken) && taken.Id != existing.Id)
            {
                warnings.Add($"Didn't rename {existing.Username} to {d.Username}: TapQueue already has a {d.Username}.");
            }
            else
            {
                changes.Add(new Change(new(DirectoryChangeAction.UpdateUser, d.Username, $"Renamed user {existing.Username} to {d.Username}, as in Active Directory."),
                    () => users.SetUsername(existing.Id, d.Username)));
                byName.Remove(existing.Username);
                byName[d.Username] = existing;
            }
        }
        if (existing.DisplayName != d.DisplayName)
            changes.Add(new Change(new(DirectoryChangeAction.UpdateUser, d.Username, $"Changed {d.Username}'s name to {d.DisplayName}, as in Active Directory."),
                () => users.SetDisplayName(existing.Id, d.DisplayName)));

        if (wantState is not null)
        {
            if (!existing.Disabled)
                changes.Add(new Change(new(DirectoryChangeAction.DisableUser, d.Username, $"Disabled {d.Username}: {Why(wantState)}. Their held jobs are kept."),
                    () => users.SetDisabled(existing.Id, true, DisabledBy.Directory, wantState), DisablesEnabledUser: true));
            else if (existing.DisabledBy == DisabledBy.Directory && existing.DirectoryState != wantState)
                changes.Add(new Change(new(DirectoryChangeAction.UpdateUser, d.Username, $"{d.Username} is still disabled: {Why(wantState)}."),
                    () => users.SetDirectoryState(existing.Id, wantState)));
        }
        else if (existing.DisabledBy == DisabledBy.Directory)
        {
            changes.Add(new Change(new(DirectoryChangeAction.EnableUser, d.Username, $"Re-enabled {d.Username}: they're enabled in Active Directory and in the scope again."),
                () => users.SetDisabled(existing.Id, false)));
        }
    }

    private void PlanBadge(UserRecord? existing, DirectoryUser d, List<BadgeRecord> allBadges, Dictionary<string, string> claimedCards,
        Dictionary<string, long> ids, List<Change> changes, List<string> warnings)
    {
        var theirs = existing is null ? [] : allBadges.Where(b => b.UserId == existing.Id).ToList();
        if (d.Badge is null)
        {
            foreach (var old in theirs.Where(b => b.Source == UserSource.ActiveDirectory))
                changes.Add(new Change(new(DirectoryChangeAction.Badge, d.Username, $"Removed card {old.CardHint} from {d.Username}: it's no longer in Active Directory."),
                    () => badges.Delete(old.Id)));
            return;
        }

        var card = BadgeStore.Normalize(d.Badge);
        var hash = BadgeStore.HashOf(card);
        if (claimedCards.TryGetValue(hash, out var claimant))
        {
            warnings.Add($"Didn't give card {BadgeStore.Hint(card)} to {d.Username}: {claimant} has the same card number in Active Directory.");
            return;
        }
        claimedCards[hash] = d.Username;

        var holder = allBadges.FirstOrDefault(b => b.CardHash == hash);
        if (holder is null)
            changes.Add(new Change(new(DirectoryChangeAction.Badge, d.Username, $"Linked card {BadgeStore.Hint(card)} to {d.Username}, from Active Directory."),
                () => badges.Add(ids[d.Guid], card, "", UserSource.ActiveDirectory)));
        else if (existing is null || holder.UserId != existing.Id)
            changes.Add(new Change(new(DirectoryChangeAction.Badge, d.Username, $"Moved card {holder.CardHint} from {holder.Username} to {d.Username}, as in Active Directory."),
                () => badges.Assign(holder.Id, ids[d.Guid], UserSource.ActiveDirectory)));
        else if (holder.Source != UserSource.ActiveDirectory)
            changes.Add(new Change(new(DirectoryChangeAction.Badge, d.Username, $"Card {holder.CardHint} of {d.Username} now comes from Active Directory."),
                () => badges.Assign(holder.Id, existing.Id, UserSource.ActiveDirectory)));

        foreach (var other in theirs.Where(b => b.CardHash != hash))
            changes.Add(new Change(new(DirectoryChangeAction.Badge, d.Username,
                    $"Removed card {other.CardHint} from {d.Username}: their card in Active Directory is {BadgeStore.Hint(card)}."),
                () => badges.Delete(other.Id)));
    }

    private List<Change> PlanGroups(DirectorySnapshot snapshot, Dictionary<string, long> ids)
    {
        var changes = new List<Change>();
        var all = groups.List();
        var byGuid = all.Where(g => g.FromDirectory && g.ExternalId is not null).ToDictionary(g => g.ExternalId!, StringComparer.OrdinalIgnoreCase);
        var takenIds = all.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usernames = snapshot.Users.ToDictionary(u => u.Guid, u => u.Username, StringComparer.OrdinalIgnoreCase);

        foreach (var g in snapshot.Groups)
        {
            var wanted = g.Members.Where(m => usernames.ContainsKey(m.UserGuid)).ToList();
            void SetMembers(string groupId) =>
                groups.SetMembers(groupId, wanted.Where(m => ids.ContainsKey(m.UserGuid)).Select(m => (ids[m.UserGuid], m.Via)));

            if (byGuid.GetValueOrDefault(g.Guid) is not { } existing)
            {
                var id = UniqueId(g.SamAccountName, takenIds);
                changes.Add(new Change(new(DirectoryChangeAction.CreateGroup, id,
                        $"Added group \"{g.Name}\" ({id}) from Active Directory with {Count(wanted.Count, "member")}; it may use every queue and printer until that's changed."),
                    () =>
                    {
                        groups.Create(id, g.Name, g.Description);
                        groups.SetSource(id, UserSource.ActiveDirectory, g.Guid);
                        SetMembers(id);
                    }));
                continue;
            }

            if (existing.Name != g.Name || existing.Description != g.Description)
                changes.Add(new Change(new(DirectoryChangeAction.UpdateGroup, existing.Id,
                        existing.Name != g.Name ? $"Renamed group \"{existing.Name}\" to \"{g.Name}\", as in Active Directory." : $"Updated the description of group \"{g.Name}\" from Active Directory."),
                    () => groups.Update(existing.Id, g.Name, g.Description)));

            var current = groups.MembersWithVia(existing.Id).ToDictionary(m => m.Username, m => m.Via, StringComparer.OrdinalIgnoreCase);
            var next = wanted.ToDictionary(m => usernames[m.UserGuid], m => m.Via, StringComparer.OrdinalIgnoreCase);
            var added = next.Keys.Where(u => !current.ContainsKey(u)).Order().ToList();
            var removed = current.Keys.Where(u => !next.ContainsKey(u)).Order().ToList();
            var moved = next.Where(m => current.TryGetValue(m.Key, out var via) && via != m.Value).Select(m => m.Key).ToList();
            if (added.Count + removed.Count + moved.Count > 0)
            {
                var parts = new List<string>();
                if (added.Count > 0) parts.Add("added " + string.Join(", ", added));
                if (removed.Count > 0) parts.Add("removed " + string.Join(", ", removed));
                if (moved.Count > 0) parts.Add($"{string.Join(", ", moved)} now through a different group");
                changes.Add(new Change(new(DirectoryChangeAction.Members, existing.Id, $"Group \"{g.Name}\": {string.Join("; ", parts)}."),
                    () => SetMembers(existing.Id)));
            }
        }

        var inScope = snapshot.Groups.Select(g => g.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in byGuid.Values.Where(g => !inScope.Contains(g.ExternalId!)))
            changes.Add(new Change(new(DirectoryChangeAction.DeleteGroup, gone.Id,
                    $"Removed group \"{gone.Name}\" ({gone.Id}): it's no longer in the Active Directory sync's scope."),
                () => groups.Delete(gone.Id)));
        return changes;
    }

    /// <summary>A TapQueue group id from the AD group's name: plain characters, lower case, not already used.</summary>
    internal static string UniqueId(string samAccountName, HashSet<string> taken)
    {
        var plain = new string(samAccountName.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (plain.Length == 0) plain = "ad-group";
        if (plain.Length > 60) plain = plain[..60];
        var id = plain;
        for (var n = 2; taken.Contains(id); n++)
            id = $"{plain}-{n}";
        taken.Add(id);
        return id;
    }

    private DirectorySyncResultDto Fail(long runId, bool dryRun, string actor, string error)
    {
        store.FinishRun(runId, DirectoryRunOutcome.Failed, "Nothing changed.", error);
        logger.LogWarning("Directory sync failed: {Error}", error);
        if (!dryRun)
            events.Record(EventCategory.Directory, actor, null, $"Active Directory sync failed, nothing changed: {error}");
        return new DirectorySyncResultDto(runId, false, DirectoryRunOutcome.Failed, "Nothing changed.", error, 0, 0, [], []);
    }

    private static DirectorySyncResultDto Result(long runId, bool applied, string outcome, string summary, string? error,
        DirectorySnapshot snapshot, List<Change> changes, List<string> warnings) =>
        new(runId, applied, outcome, summary, error, snapshot.Users.Count, snapshot.Groups.Count, changes.Select(c => c.Dto).ToList(), warnings);

    private static string Summarize(List<Change> changes, List<string> warnings)
    {
        int Of(params string[] actions) => changes.Count(c => actions.Contains(c.Dto.Action));
        var parts = new List<string>();
        void Add(int n, string one, string many) { if (n > 0) parts.Add(n == 1 ? $"1 {one}" : $"{n} {many}"); }
        Add(Of(DirectoryChangeAction.CreateUser), "user added", "users added");
        Add(Of(DirectoryChangeAction.LinkUser), "local user linked", "local users linked");
        Add(changes.Where(c => c.Dto.Action == DirectoryChangeAction.UpdateUser).Select(c => c.Dto.Subject).Distinct().Count(), "user updated", "users updated");
        Add(Of(DirectoryChangeAction.DisableUser), "user disabled", "users disabled");
        Add(Of(DirectoryChangeAction.EnableUser), "user re-enabled", "users re-enabled");
        Add(Of(DirectoryChangeAction.Badge), "card change", "card changes");
        Add(Of(DirectoryChangeAction.CreateGroup), "group added", "groups added");
        Add(changes.Where(c => c.Dto.Action is DirectoryChangeAction.UpdateGroup or DirectoryChangeAction.Members).Select(c => c.Dto.Subject).Distinct().Count(),
            "group changed", "groups changed");
        Add(Of(DirectoryChangeAction.DeleteGroup), "group removed", "groups removed");
        Add(warnings.Count, "warning", "warnings");
        return parts.Count == 0 ? "Everything is up to date." : string.Join(", ", parts) + ".";
    }

    private static string Subject(DirectoryChangeDto change) => change.Action switch
    {
        DirectoryChangeAction.CreateGroup or DirectoryChangeAction.UpdateGroup or DirectoryChangeAction.DeleteGroup or DirectoryChangeAction.Members
            => EventLog.Group(change.Subject),
        _ => EventLog.User(change.Subject),
    };

    private static string Why(string state) => state switch
    {
        DirectoryState.Disabled => "disabled in Active Directory",
        DirectoryState.Expired => "their account expired in Active Directory",
        _ => "no longer in the Active Directory sync's scope",
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
