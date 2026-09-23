namespace TapQueue.Server.Data;

/// <summary>
/// Who may print to which queue and release at which printer. A user in no group may use everything,
/// so nothing changes until groups are set up. A user in groups may use what any of them allows.
/// </summary>
public sealed class AccessPolicy(Database database)
{
    public bool CanPrintTo(long userId, string queueId) => Allowed(userId, "all_queues", "group_queues", "queue_id", queueId);

    public bool CanReleaseAt(long userId, string printerId) => Allowed(userId, "all_printers", "group_printers", "printer_id", printerId);

    private bool Allowed(long userId, string allColumn, string table, string idColumn, string id) =>
        Convert.ToBoolean(database.Scalar($"""
            SELECT NOT EXISTS (SELECT 1 FROM group_members WHERE user_id = $u)
                OR EXISTS (
                    SELECT 1 FROM group_members m JOIN groups g ON g.id = m.group_id
                    WHERE m.user_id = $u
                      AND (g.{allColumn} OR EXISTS (SELECT 1 FROM {table} t WHERE t.group_id = g.id AND t.{idColumn} = $id COLLATE NOCASE)))
            """, ("$u", userId), ("$id", id)));
}
