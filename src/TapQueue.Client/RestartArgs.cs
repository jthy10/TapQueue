namespace TapQueue.Client;

/// <summary>
/// What a tray app adds to its command line when it starts its replacement: after an update, or
/// for "Refresh TapQueue" in its menu.
/// </summary>
/// <param name="AppArgs">The command line without any of it.</param>
/// <param name="WaitFor">The process being replaced, which has to exit first (it holds the one-per-user lock).</param>
/// <param name="UpdatedFrom">The version this one replaced, when an update started it.</param>
/// <param name="Refreshed">What "Refresh TapQueue" did to the printers, to tell the user once the new tray app is up.</param>
/// <param name="RefreshFailed">True when <paramref name="Refreshed"/> is bad news.</param>
public sealed record RestartArgs(string[] AppArgs, int? WaitFor, string? UpdatedFrom, string? Refreshed, bool RefreshFailed)
{
    public const string WaitForArg = "--wait-for";
    public const string UpdatedFromArg = "--updated-from";
    public const string RefreshedArg = "--refreshed";
    public const string RefreshFailedArg = "--refresh-failed";

    public static RestartArgs Parse(string[] args)
    {
        var appArgs = new List<string>();
        int? waitFor = null;
        string? updatedFrom = null, refreshed = null;
        var failed = false;
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            if (args[i] == WaitForArg && hasValue && int.TryParse(args[i + 1], out var pid))
                waitFor = pid;
            else if (args[i] == UpdatedFromArg && hasValue)
                updatedFrom = args[i + 1];
            else if (args[i] is RefreshedArg or RefreshFailedArg && hasValue)
                (refreshed, failed) = (args[i + 1], args[i] == RefreshFailedArg);
            else
            {
                appArgs.Add(args[i]);
                continue;
            }
            i++;
        }
        return new RestartArgs([.. appArgs], waitFor, updatedFrom, refreshed, failed);
    }

    /// <summary>The command line for the tray app that replaces process <paramref name="pid"/> after an update.</summary>
    public static IEnumerable<string> AfterUpdate(string[] appArgs, int pid, string fromVersion) =>
        [.. appArgs, WaitForArg, pid.ToString(), UpdatedFromArg, fromVersion];

    /// <summary>The command line for the tray app that replaces process <paramref name="pid"/> for "Refresh TapQueue".</summary>
    public static IEnumerable<string> AfterRefresh(string[] appArgs, int pid, string result, bool failed) =>
        [.. appArgs, WaitForArg, pid.ToString(), failed ? RefreshFailedArg : RefreshedArg, result];

    /// <summary>What "Refresh TapQueue" says about the printers, from the service's answer.</summary>
    public static (string Text, bool Failed) Describe(PrinterRefreshReply reply) => reply.Success
        ? (reply.Printers == 0
            ? "TapQueue reloaded. Your TapQueue server has no printers for this PC."
            : $"TapQueue reloaded and {reply.Printers} TapQueue printer{(reply.Printers == 1 ? "" : "s")} reinstalled.", false)
        : ($"TapQueue reloaded, but the printers couldn't be reinstalled: {reply.Error ?? "the TapQueue service didn't say why."}", true);

    /// <summary>The same, when the service couldn't be asked at all.</summary>
    public static (string Text, bool Failed) DescribeFailure(string why) =>
        ($"TapQueue reloaded, but the printers couldn't be reinstalled: {why}", true);

    public const string ServiceNotRunning = "the TapQueue service isn't running on this PC. Ask your IT team to reinstall TapQueue.";
}
