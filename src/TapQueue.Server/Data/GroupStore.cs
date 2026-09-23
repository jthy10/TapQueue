using System.Text.Json;
using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="AllQueues">Members may print to every queue; otherwise only to <paramref name="QueueIds"/>.</param>
/// <param name="AllPrinters">Members may release at every printer; otherwise only at <paramref name="PrinterIds"/>.</param>
public sealed record GroupRecord(
    string Id,
    string Name,
    string Description,
    bool AllQueues,
    IReadOnlyList<string> QueueIds,
    bool AllPrinters,
    IReadOnlyList<string> PrinterIds,
    int MemberCount,
    string Source)
{
    public GroupDto ToDto() => new(Id, Name, Description, AllQueues, QueueIds, AllPrinters, PrinterIds, MemberCount, Source);
}

/// <summary>Groups of users and what they may use. See <see cref="AccessPolicy"/> for how that's applied.</summary>
public sealed class GroupStore(Database database)
{
    private const string Select = """
        SELECT g.id, g.name, g.description, g.all_queues,
               (SELECT json_group_array(queue_id) FROM group_queues WHERE group_id = g.id),
               g.all_printers,
               (SELECT json_group_array(printer_id) FROM group_printers WHERE group_id = g.id),
               (SELECT COUNT(*) FROM group_members WHERE group_id = g.id),
               g.source
        FROM groups g
        """;

    public GroupRecord? Get(string id) => database.QueryOne(Select + " WHERE g.id = $id", Map, ("$id", id));

    public List<GroupRecord> List() => database.Query(Select + " ORDER BY g.name COLLATE NOCASE", Map);

    public GroupRecord Create(string id, string name, string description)
    {
        database.Execute("INSERT INTO groups (id, name, description, created_at) VALUES ($id, $n, $d, $now)",
            ("$id", id), ("$n", name), ("$d", description), ("$now", DateTimeOffset.UtcNow));
        return Get(id)!;
    }

    public void Update(string id, string name, string description) =>
        database.Execute("UPDATE groups SET name = $n, description = $d WHERE id = $id", ("$n", name), ("$d", description), ("$id", id));

    /// <summary>Replaces what the group may print to. The caller checks the queues exist.</summary>
    public void SetQueues(string id, bool all, IEnumerable<string> queueIds) =>
        database.ExecuteAtomically("""
            UPDATE groups SET all_queues = $all WHERE id = $id;
            DELETE FROM group_queues WHERE group_id = $id;
            INSERT INTO group_queues (group_id, queue_id) SELECT $id, value FROM json_each($ids) WHERE NOT $all;
            """, ("$id", id), ("$all", all), ("$ids", JsonSerializer.Serialize(queueIds.Distinct(StringComparer.OrdinalIgnoreCase))));

    /// <summary>Replaces where the group may release. The caller checks the printers exist.</summary>
    public void SetPrinters(string id, bool all, IEnumerable<string> printerIds) =>
        database.ExecuteAtomically("""
            UPDATE groups SET all_printers = $all WHERE id = $id;
            DELETE FROM group_printers WHERE group_id = $id;
            INSERT INTO group_printers (group_id, printer_id) SELECT $id, value FROM json_each($ids) WHERE NOT $all;
            """, ("$id", id), ("$all", all), ("$ids", JsonSerializer.Serialize(printerIds.Distinct(StringComparer.OrdinalIgnoreCase))));

    public bool Delete(string id) => database.Execute("DELETE FROM groups WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Returns false if they were already a member.</summary>
    public bool AddMember(string groupId, long userId) =>
        database.Execute("INSERT OR IGNORE INTO group_members (group_id, user_id) VALUES ($g, $u)", ("$g", groupId), ("$u", userId)) == 1;

    public bool RemoveMember(string groupId, long userId) =>
        database.Execute("DELETE FROM group_members WHERE group_id = $g AND user_id = $u", ("$g", groupId), ("$u", userId)) == 1;

    public List<string> Members(string groupId) =>
        database.Query("""
            SELECT u.username FROM group_members m JOIN users u ON u.id = m.user_id
            WHERE m.group_id = $g ORDER BY u.username
            """, r => r.GetString(0), ("$g", groupId));

    /// <summary>Every user's group ids, for listing users.</summary>
    public Dictionary<long, List<string>> Memberships() =>
        database.Query("SELECT user_id, group_id FROM group_members ORDER BY group_id", r => (User: r.GetInt64(0), Group: r.GetString(1)))
            .GroupBy(m => m.User)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Group).ToList());

    private static GroupRecord Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetBoolean(3), Ids(r.GetString(4)),
        r.GetBoolean(5), Ids(r.GetString(6)),
        r.GetInt32(7), r.GetString(8));

    private static List<string> Ids(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
