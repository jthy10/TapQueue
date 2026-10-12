using System.Diagnostics;
using TapQueue.Shared;
using TapQueue.Shared.Api;
using Timer = System.Windows.Forms.Timer;

namespace TapQueue.Client.Windows;

/// <summary>
/// The tray app, one per signed-in Windows user: keeps a session open with the server (which is
/// how the server knows whose print jobs come from this PC) and shows their printers and jobs. Printers and
/// updates are handled for the whole PC by the TapQueue service (<see cref="MachineService"/>).
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly string[] _args;
    private readonly TapQueueApi _api;
    private readonly NotifyIcon _tray;
    private readonly Icon _connectedIcon = TrayIcon.Create(connected: true);
    private readonly Icon _disconnectedIcon = TrayIcon.Create(connected: false);
    private readonly Timer _pollTimer = new() { Interval = 5000 };
    private readonly Timer _heartbeatTimer = new();
    private readonly Timer _retryTimer = new() { Interval = (int)RetryDelay.TotalMilliseconds };

    private List<JobDto> _jobs = [];
    private List<JobDto> _heldJobs = [];
    private List<PrinterDto>? _printers;
    private List<QueueDto>? _queues;
    private HashSet<long>? _knownJobIds;
    private bool _connected;
    private bool _signedOut;
    private bool _needsSignIn;
    private readonly DateTime _exeWrittenAt = File.GetLastWriteTimeUtc(Environment.ProcessPath!);
    private bool _polling;
    private bool _checkingForUpdates;
    private bool _refreshing;
    private string? _lastError;
    private PrintersAndQueueForm? _window;
    private SignInForm? _signInForm;

    /// <param name="start">The command line, and what the tray app this one replaced had to say (<see cref="RestartArgs"/>).</param>
    public TrayApp(ClientConfig config, RestartArgs start)
    {
        _args = start.AppArgs;
        _api = new TapQueueApi(config) { VerifySession = UpdateCheckPipe.VerifySessionAsync };
        _tray = new NotifyIcon
        {
            Icon = _disconnectedIcon,
            Text = "TapQueue — connecting…",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        _tray.DoubleClick += (_, _) =>
        {
            if (_needsSignIn) ShowSignInForm();
            else ShowWindow(queue: true);
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_needsSignIn) ShowSignInForm();
        };

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _retryTimer.Tick += async (_, _) => await ConnectAsync();

        BuildMenu();
        if (start.UpdatedFrom is not null)
            Notify("TapQueue updated", $"Now running {TapQueueVersion.Current} (was {start.UpdatedFrom}).");
        if (start.Refreshed is not null)
            Notify("TapQueue refreshed", start.Refreshed, start.RefreshFailed ? ToolTipIcon.Warning : ToolTipIcon.Info);
        _ = ConnectAsync();
    }

    public TapQueueApi Api => _api;
    public bool Connected => _connected;

    /// <summary>The printers this person can pick up at, as of the last look (<see cref="RefreshAsync"/>) or sign-in.</summary>
    public IReadOnlyList<PrinterDto> Printers => !_connected ? [] : _printers ?? _api.Session?.Printers ?? [];

    /// <summary>The queues this person can print to, likewise.</summary>
    public IReadOnlyList<QueueDto> Queues => !_connected ? [] : _queues ?? _api.Session?.Queues ?? [];

    /// <summary>The jobs "My print queue" lists (<see cref="PrintQueue.Visible"/>).</summary>
    public IReadOnlyList<JobDto> Jobs => _jobs;
    public IReadOnlyList<JobDto> HeldJobs => _heldJobs;

    private async Task ConnectAsync()
    {
        // Also checked here, not only on heartbeats, so a tray app that can't reach the server
        // (or was signed out) still restarts into a newly installed build.
        if (RestartIfUpdated())
            return;
        _retryTimer.Stop();
        _signedOut = false;
        try
        {
            await OnSignedInAsync(await _api.SignInAsync());
        }
        catch (SignInRequiredException ex)
        {
            if (!_connected) // unless "Sign in as…" finished first
                NeedSignIn(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            SetConnected(false, ex.Message);
            _retryTimer.Start();
        }
    }

    private async Task OnSignedInAsync(ClientSessionResponse session)
    {
        _needsSignIn = false;
        _knownJobIds = null;
        (_printers, _queues) = (null, null);
        SetConnected(true, null);
        _heartbeatTimer.Interval = Math.Max(10, session.HeartbeatSeconds) * 1000;
        _heartbeatTimer.Start();
        _pollTimer.Start();
        await PollAsync();
    }

    /// <summary>
    /// The server wants a domain account and none is remembered. Keep checking now and then, in case
    /// an admin switches back to signing in as the PC's user.
    /// </summary>
    private void NeedSignIn(string message)
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        (_jobs, _heldJobs) = ([], []);
        if (!_needsSignIn)
            Notify("Sign in to TapQueue", "Click here, or right-click the TapQueue icon and choose Sign in as…, to print with your domain account.");
        _needsSignIn = true;
        SetConnected(false, message);
        _retryTimer.Start();
    }

    /// <summary>"Sign in as…". Returns why it didn't work, or null if it did.</summary>
    private async Task<string?> SignInAsAsync(string username, string password)
    {
        _retryTimer.Stop();
        try
        {
            var session = await _api.SignInWithPasswordAsync(username, password);
            _signedOut = false;
            await OnSignedInAsync(session);
            Notify("Signed in", $"Print jobs from this PC now go to {session.User.DisplayName}.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            if (!_connected)
                _retryTimer.Start();
            return ex is HttpRequestException or TaskCanceledException ? $"Can't reach the TapQueue server: {ex.Message}" : ex.Message;
        }
    }

    private async Task SignOutAsync()
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        _retryTimer.Stop();
        await _api.SignOutAsync();
        _needsSignIn = true; // no notification: they just did this
        (_jobs, _heldJobs) = ([], []);
        SetConnected(false, null);
        _retryTimer.Start();
    }

    private async Task PollAsync()
    {
        if (_polling || !_connected) return;
        _polling = true;
        try
        {
            _jobs = PrintQueue.Visible(await _api.GetJobsAsync(), DateTimeOffset.UtcNow);
            _heldJobs = _jobs.Where(PrintQueue.IsHeld).ToList();
            var ids = _heldJobs.Select(j => j.Id).ToHashSet();
            if (_knownJobIds is not null)
            {
                var added = _heldJobs.Where(j => !_knownJobIds.Contains(j.Id)).ToList();
                if (added.Count == 1)
                    Notify("Print job held", $"\"{added[0].Name}\" is waiting. Release it at any TapQueue printer.");
                else if (added.Count > 1)
                    Notify("Print jobs held", $"{added.Count} jobs are waiting. Release them at any TapQueue printer.");
            }
            _knownJobIds = ids;
            UpdateTooltip();
            _window?.RefreshView();
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            OnConnectionLost(ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task HeartbeatAsync()
    {
        if (RestartIfUpdated())
            return;
        try
        {
            await _api.HeartbeatAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            OnConnectionLost(ex);
        }
    }

    /// <summary>Asks the server again for this person's printers, queues and jobs.</summary>
    public async Task RefreshAsync()
    {
        if (!_connected) return;
        try
        {
            _printers = await _api.GetPrintersAsync();
            _queues = await _api.GetQueuesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            // The poll below finds out the same way, and deals with it.
        }
        await PollAsync();
    }

    public async Task<ReleaseResponse?> ReleaseAsync(string printerId, IReadOnlyList<long>? jobIds)
    {
        try
        {
            var response = await _api.ReleaseAsync(printerId, jobIds);
            var ok = response.Results.Count(r => r.Success);
            var failed = response.Results.Where(r => !r.Success).ToList();
            var printer = Printers.FirstOrDefault(p => p.Id == printerId)?.Name ?? printerId;
            if (response.Results.Count == 0)
                Notify("Nothing to release", "You have no held print jobs.");
            else if (failed.Count == 0)
                Notify("Released", $"{ok} job{(ok == 1 ? "" : "s")} sent to {printer}."
                    + string.Concat(response.Results.Select(r => r.Note).OfType<string>().Distinct().Select(note => "\n" + note)));
            else
                Notify("Some jobs didn't print", string.Join("\n", failed.Select(f => $"{f.JobName}: {f.Error}")), ToolTipIcon.Warning);
            await PollAsync();
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            Notify("Release failed", ex.Message, ToolTipIcon.Error);
            return null;
        }
    }

    /// <summary>
    /// Deletes jobs that are waiting and stops ones already sent to a printer. Returns what's worth
    /// telling the person: what was stopped and given back, and anything that couldn't be done.
    /// </summary>
    public async Task<List<string>> DeleteAsync(IReadOnlyList<JobDto> jobs)
    {
        var notes = await PrintQueue.DeleteAsync(_api, jobs);
        await PollAsync();
        return notes;
    }

    /// <summary>
    /// Asks the TapQueue service to check the server for a new client and install it
    /// (<see cref="UpdateCheckPipe"/>); once it has, restarts into it.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)); // a slow download
            var reply = await UpdateCheckPipe.CheckAsync(timeout.Token);
            switch (reply.Outcome)
            {
                case UpdateOutcome.UpToDate:
                    Notify("TapQueue is up to date", $"You have {TapQueueVersion.Current}, the version your TapQueue server provides.");
                    break;
                case UpdateOutcome.NoBuild:
                    Notify("No updates", "Your TapQueue server doesn't provide client updates.");
                    break;
                case UpdateOutcome.Installed:
                    if (!RestartIfUpdated())
                        Notify("TapQueue updated", $"Installed {reply.Version}. It starts next time you sign in.");
                    break;
                default:
                    Notify("Update failed", reply.Error ?? "The TapQueue service couldn't install the update.", ToolTipIcon.Error);
                    break;
            }
        }
        catch (TimeoutException)
        {
            Notify("Can't check for updates", "The TapQueue service isn't running on this PC. Ask your IT team to reinstall TapQueue.", ToolTipIcon.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Notify("Can't check for updates", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    /// <summary>
    /// "Refresh TapQueue": asks the TapQueue service to remove and add this PC's TapQueue printers
    /// again (<see cref="UpdateCheckPipe"/>), for when one has gone missing or stopped working, then
    /// starts the tray app afresh, which signs in again and says how the printers went.
    /// </summary>
    private async Task RefreshTapQueueAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        (string Text, bool Failed) result;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)); // each printer can take a while
            result = RestartArgs.Describe(await UpdateCheckPipe.RefreshPrintersAsync(timeout.Token));
        }
        catch (TimeoutException)
        {
            result = RestartArgs.DescribeFailure(RestartArgs.ServiceNotRunning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            result = RestartArgs.DescribeFailure(ex.Message);
        }
        if (StartNext(RestartArgs.AfterRefresh(_args, Environment.ProcessId, result.Text, result.Failed)))
            return;
        // Couldn't start again (the exe is being replaced by an update): sign in again instead.
        _refreshing = false;
        Notify("TapQueue refreshed", result.Text, result.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
        await ConnectAsync();
    }

    /// <summary>
    /// The TapQueue service replaces TapQueueClient.exe when an update is published. Start the new
    /// exe (it waits for this one to exit) and exit.
    /// </summary>
    private bool RestartIfUpdated()
    {
        var exe = Environment.ProcessPath!;
        if (!File.Exists(exe) || File.GetLastWriteTimeUtc(exe) == _exeWrittenAt)
            return false;
        return StartNext(RestartArgs.AfterUpdate(_args, Environment.ProcessId, TapQueueVersion.Current));
    }

    /// <summary>Starts this program again with <paramref name="args"/> and exits. False if it couldn't be started.</summary>
    private bool StartNext(IEnumerable<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // mid-swap; an update tries again next heartbeat
        }
        ExitThread();
        return true;
    }

    private void OnConnectionLost(Exception ex)
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        if (ex is SignInRequiredException)
        {
            // The session lapsed and the server no longer knows the remembered domain sign-in.
            NeedSignIn(ex.Message);
            return;
        }
        if (ex is TapQueueApiException { Status: System.Net.HttpStatusCode.Forbidden })
        {
            // An admin signed this session out. Stay out (jobs printed here no longer go to this
            // user) until the user signs in again from the menu or Windows logs them in again.
            _signedOut = true;
            SetConnected(false, ex.Message);
            Notify("Signed out of TapQueue", ex.Message, ToolTipIcon.Warning);
            return;
        }
        SetConnected(false, ex.Message);
        _retryTimer.Start();
    }

    private void SetConnected(bool connected, string? error)
    {
        _connected = connected;
        _lastError = error;
        _tray.Icon = connected ? _connectedIcon : _disconnectedIcon;
        UpdateTooltip();
        _window?.RefreshView();
    }

    private void UpdateTooltip()
    {
        var text = _connected ? $"TapQueue — {_heldJobs.Count} held job{(_heldJobs.Count == 1 ? "" : "s")}"
            : _needsSignIn ? "TapQueue — not signed in"
            : _signedOut ? "TapQueue — signed out"
            : "TapQueue — can't reach server";
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        var status = _connected ? $"Signed in as {_api.Session?.User.DisplayName}"
            : _needsSignIn ? "Not signed in"
            : _signedOut ? "Signed out by an admin"
            : $"Not connected{(_lastError is null ? "" : $": {Shorten(_lastError, 60)}")}";
        menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"{_heldJobs.Count} held job{(_heldJobs.Count == 1 ? "" : "s")}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var releaseAll = new ToolStripMenuItem("Release all to") { Enabled = _connected && _heldJobs.Count > 0 };
        foreach (var printer in Printers)
        {
            var label = printer.Online ? printer.Name : $"{printer.Name} (offline)";
            releaseAll.DropDownItems.Add(label, null, async (_, _) => await ReleaseAsync(printer.Id, null));
        }
        menu.Items.Add(releaseAll);
        menu.Items.Add("View printers and queue…", null, (_, _) => ShowWindow(queue: false));
        if (!_connected && !_needsSignIn)
            menu.Items.Add(_signedOut ? "Sign in again" : "Retry connection", null, async (_, _) => await ConnectAsync());
        menu.Items.Add(new ToolStripSeparator());
        // Only when the server asks for a domain account; otherwise TapQueue signs in as the PC's user.
        var domain = _api.SignInMode == ClientSignIn.Domain;
        menu.Items.Add(new ToolStripMenuItem("Sign in as…", null, (_, _) => ShowSignInForm())
        {
            Enabled = domain,
            ToolTipText = domain ? null : "Your IT team has TapQueue sign you in as the person signed in to this PC.",
        });
        if (domain && _api.Remembered is not null)
            menu.Items.Add("Sign out", null, async (_, _) => await SignOutAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"TapQueue {TapQueueVersion.Current}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(_refreshing ? "Refreshing TapQueue…" : "Refresh TapQueue", null,
            async (_, _) => await RefreshTapQueueAsync())
        {
            Enabled = !_refreshing,
            ToolTipText = "Reinstalls this PC's TapQueue printers and reloads TapQueue.",
        });
        menu.Items.Add(new ToolStripMenuItem(_checkingForUpdates ? "Checking for updates…" : "Check for updates", null,
            async (_, _) => await CheckForUpdatesAsync()) { Enabled = !_checkingForUpdates });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void ShowSignInForm()
    {
        if (_signInForm is { IsDisposed: false })
        {
            _signInForm.Activate();
            return;
        }
        _signInForm = new SignInForm(SignInAsAsync, _api.Remembered?.Username);
        _signInForm.Show();
        _signInForm.Activate();
    }

    /// <param name="queue">Open on "My print queue" rather than "Printers".</param>
    private void ShowWindow(bool queue)
    {
        if (_window is { IsDisposed: false })
        {
            if (queue) _window.ShowQueue();
            _window.Activate();
            return;
        }
        _window = new PrintersAndQueueForm(this, queue);
        _window.Show();
    }

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _tray.ShowBalloonTip(5000, title, text, icon);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _pollTimer.Dispose();
        _heartbeatTimer.Dispose();
        _retryTimer.Dispose();
        _api.Dispose();
        base.ExitThreadCore();
    }
}
