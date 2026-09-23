using TapQueue.Shared;
using TapQueue.Shared.Api;
using Timer = System.Windows.Forms.Timer;

namespace TapQueue.Client.Windows;

/// <summary>
/// The tray app: keeps a session open with the server (which is how the server knows whose
/// print jobs come from this PC), installs the print queue, and shows held jobs.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly ClientConfig _config;
    private readonly TapQueueApi _api;
    private readonly NotifyIcon _tray;
    private readonly Icon _connectedIcon = TrayIcon.Create(connected: true);
    private readonly Icon _disconnectedIcon = TrayIcon.Create(connected: false);
    private readonly Timer _pollTimer = new() { Interval = 5000 };
    private readonly Timer _heartbeatTimer = new();
    private readonly Timer _retryTimer = new() { Interval = (int)RetryDelay.TotalMilliseconds };

    private List<JobDto> _heldJobs = [];
    private HashSet<long>? _knownJobIds;
    private bool _connected;
    private bool _printersChecked;
    private bool _polling;
    private string? _lastError;
    private JobsForm? _jobsForm;

    public TrayApp(ClientConfig config)
    {
        _config = config;
        _api = new TapQueueApi(config);
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
        _tray.DoubleClick += (_, _) => ShowJobsForm();

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _retryTimer.Tick += async (_, _) => await ConnectAsync();

        BuildMenu();
        _ = ConnectAsync();
    }

    public TapQueueApi Api => _api;
    public IReadOnlyList<PrinterDto> Printers => _api.Session?.Printers ?? [];
    public IReadOnlyList<JobDto> HeldJobs => _heldJobs;

    private async Task ConnectAsync()
    {
        _retryTimer.Stop();
        try
        {
            var session = await _api.SignInAsync();
            SetConnected(true, null);
            _heartbeatTimer.Interval = Math.Max(10, session.HeartbeatSeconds) * 1000;
            _heartbeatTimer.Start();
            _pollTimer.Start();

            if (!_printersChecked && _config.InstallPrinters)
            {
                _printersChecked = true;
                await InstallPrintersAsync(reinstall: false);
            }
            await PollAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            SetConnected(false, ex.Message);
            _retryTimer.Start();
        }
    }

    private async Task PollAsync()
    {
        if (_polling || !_connected) return;
        _polling = true;
        try
        {
            _heldJobs = await _api.GetHeldJobsAsync();
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
            _jobsForm?.RefreshJobs();
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
        try
        {
            await _api.HeartbeatAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            OnConnectionLost(ex);
        }
    }

    public async Task RefreshAsync() => await PollAsync();

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
                Notify("Released", $"{ok} job{(ok == 1 ? "" : "s")} sent to {printer}.");
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

    public async Task CancelAsync(IEnumerable<long> jobIds)
    {
        try
        {
            foreach (var id in jobIds)
                await _api.CancelJobAsync(id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            Notify("Couldn't cancel", ex.Message, ToolTipIcon.Error);
        }
        await PollAsync();
    }

    private async Task InstallPrintersAsync(bool reinstall)
    {
        foreach (var queue in _api.Session?.Queues ?? [])
        {
            var (success, output) = await PrinterInstaller.EnsureInstalledAsync(queue.Name, _api.IppUrl(queue), reinstall);
            if (!success)
                Notify("Couldn't add printer", $"\"{queue.Name}\": {output}\nTry running TapQueue once as administrator.", ToolTipIcon.Warning);
            else if (output.Contains("installed") && !output.Contains("already"))
                Notify("Printer added", $"You can now print to \"{queue.Name}\".");
        }
    }

    private void OnConnectionLost(Exception ex)
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        SetConnected(false, ex.Message);
        _retryTimer.Start();
    }

    private void SetConnected(bool connected, string? error)
    {
        _connected = connected;
        _lastError = error;
        _tray.Icon = connected ? _connectedIcon : _disconnectedIcon;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var text = _connected
            ? $"TapQueue — {_heldJobs.Count} held job{(_heldJobs.Count == 1 ? "" : "s")}"
            : "TapQueue — can't reach server";
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        var status = _connected
            ? $"Signed in as {_api.Session?.User.DisplayName}"
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
        menu.Items.Add("Held jobs…", null, (_, _) => ShowJobsForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reinstall TapQueue printer", null, async (_, _) => await InstallPrintersAsync(reinstall: true))
        {
            Enabled = _connected,
        });
        if (!_connected)
            menu.Items.Add("Retry connection", null, async (_, _) => await ConnectAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"TapQueue {TapQueueVersion.Current}") { Enabled = false });
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private void ShowJobsForm()
    {
        if (_jobsForm is { IsDisposed: false })
        {
            _jobsForm.Activate();
            return;
        }
        _jobsForm = new JobsForm(this);
        _jobsForm.Show();
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
