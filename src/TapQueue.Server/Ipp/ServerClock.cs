namespace TapQueue.Server.Ipp;

/// <summary>IPP times are seconds of printer-up-time; this maps wall-clock times onto that scale.</summary>
public static class ServerClock
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static int UpTimeSeconds => ToUpTime(DateTimeOffset.UtcNow);

    /// <summary>Times before this server started (jobs from a previous run) map to 1.</summary>
    public static int ToUpTime(DateTimeOffset time) =>
        (int)Math.Clamp((time - StartedAt).TotalSeconds + 1, 1, int.MaxValue);
}
