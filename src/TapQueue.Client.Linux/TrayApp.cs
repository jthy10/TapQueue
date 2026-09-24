using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using System.Diagnostics;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Linux;

/// <summary>
/// The tray app, one per signed-in desktop user: keeps a session open with the server (which is
/// how the server knows whose print jobs come from this PC) and shows held jobs. Printers and
/// updates are handled for the whole PC by the TapQueue service (<see cref="LinuxService"/>).
/// The tray icon needs a desktop that shows StatusNotifierItem/AppIndicator icons (Ubuntu's
/// GNOME does out of the box, as do KDE and most others).
/// </summary>
public sealed class TrayApp : Application
{
    public sealed record Options(ClientConfig Config, string[] Args, string? UpdatedFrom);

    /// <summary>Set by <see cref="Program"/> before Avalonia creates the app.</summary>
    public static Options Start { get; set; } = null!;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private TapQueueApi _api = null!;
    private TrayIcon _tray = null!;
    private WindowIcon _connectedIcon = null!;
    private WindowIcon _disconnectedIcon = null!;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _heartbeatTimer = new();
    private readonly DispatcherTimer _retryTimer = new() { Interval = RetryDelay };
    private readonly DateTime _exeWrittenAt = File.GetLastWriteTimeUtc(Environment.ProcessPath!);

    private List<JobDto> _heldJobs = [];
    private HashSet<long>? _knownJobIds;
    private bool _connected;
    private bool _signedOut;
    private bool _polling;
    private bool _checkingForUpdates;
    private string? _lastError;
    private JobsWindow? _jobsWindow;

    public TapQueueApi Api => _api;
    public IReadOnlyList<PrinterDto> Printers => _api.Session?.Printers ?? [];
    public IReadOnlyList<JobDto> HeldJobs => _heldJobs;
    public WindowIcon AppIcon => _connectedIcon;

    public override void Initialize()
    {
        Name = "TapQueue";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // follow the desktop's light/dark setting
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _api = new TapQueueApi(Start.Config);
        _connectedIcon = TrayIconImage.Create(connected: true);
        _disconnectedIcon = TrayIconImage.Create(connected: false);
        _tray = new TrayIcon
        {
            Icon = _disconnectedIcon,
            ToolTipText = "TapQueue — connecting…",
            Menu = [],
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowJobsWindow();
        TrayIcon.SetIcons(this, [_tray]);

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _heartbeatTimer.Tick += async (_, _) => await HeartbeatAsync();
        _retryTimer.Tick += async (_, _) => await ConnectAsync();

        BuildMenu();
        if (Start.UpdatedFrom is not null)
            Notify("TapQueue updated", $"Now running {TapQueueVersion.Current} (was {Start.UpdatedFrom}).");
        _ = ConnectAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task ConnectAsync()
    {
        _retryTimer.Stop();
        _signedOut = false;
        try
        {
            var session = await _api.SignInAsync();
            SetConnected(true, null);
            _heartbeatTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, session.HeartbeatSeconds));
            _heartbeatTimer.Start();
            _pollTimer.Start();
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
            var changed = _knownJobIds is null || !ids.SetEquals(_knownJobIds);
            _knownJobIds = ids;
            if (changed)
            {
                UpdateTooltip();
                BuildMenu();
            }
            _jobsWindow?.RefreshJobs();
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
                Notify("Some jobs didn't print", string.Join("\n", failed.Select(f => $"{f.JobName}: {f.Error}")), urgent: true);
            await PollAsync();
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
        {
            Notify("Release failed", ex.Message, urgent: true);
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
            Notify("Couldn't cancel", ex.Message, urgent: true);
        }
        await PollAsync();
    }

    /// <summary>
    /// Asks the TapQueue service to check the server for a new client and install it
    /// (<see cref="UpdateCheckSocket"/>); once it has, restarts into it.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        BuildMenu();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)); // a slow download
            var reply = await UpdateCheckSocket.CheckAsync(timeout.Token);
            switch (reply.Outcome)
            {
                case UpdateOutcome.UpToDate:
                    Notify("TapQueue is up to date", $"You have {TapQueueVersion.Current}, the version your TapQueue server provides.");
                    break;
                case UpdateOutcome.NoBuild:
                    Notify("No updates", "Your TapQueue server doesn't provide Linux client updates.");
                    break;
                case UpdateOutcome.Installed:
                    if (!RestartIfUpdated())
                        Notify("TapQueue updated", $"Installed {reply.Version}. It starts next time you sign in.");
                    break;
                default:
                    Notify("Update failed", reply.Error ?? "The TapQueue service couldn't install the update.", urgent: true);
                    break;
            }
        }
        catch (TimeoutException)
        {
            Notify("Can't check for updates", "The TapQueue service isn't running on this PC. Ask your IT team to reinstall TapQueue.", urgent: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Notify("Can't check for updates", ex.Message, urgent: true);
        }
        finally
        {
            _checkingForUpdates = false;
            BuildMenu();
        }
    }

    /// <summary>
    /// The TapQueue service replaces tapqueue-client when an update is published. Start the new
    /// program (it waits for this one to exit) and exit.
    /// </summary>
    private bool RestartIfUpdated()
    {
        var exe = Environment.ProcessPath!;
        if (!File.Exists(exe) || File.GetLastWriteTimeUtc(exe) == _exeWrittenAt)
            return false;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in Start.Args)
                start.ArgumentList.Add(arg);
            start.ArgumentList.Add(Program.WaitForArg);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(Program.UpdatedFromArg);
            start.ArgumentList.Add(TapQueueVersion.Current);
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // mid-swap; try again next heartbeat
        }
        Exit();
        return true;
    }

    private void OnConnectionLost(Exception ex)
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        if (ex is TapQueueApiException { Status: System.Net.HttpStatusCode.Forbidden })
        {
            // An admin signed this session out. Stay out (jobs printed here no longer go to this
            // user) until the user signs in again from the menu or signs in to the desktop again.
            _signedOut = true;
            SetConnected(false, ex.Message);
            Notify("Signed out of TapQueue", ex.Message, urgent: true);
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
        BuildMenu();
    }

    private void UpdateTooltip() =>
        _tray.ToolTipText = _connected ? $"TapQueue — {_heldJobs.Count} held job{(_heldJobs.Count == 1 ? "" : "s")}"
            : _signedOut ? "TapQueue — signed out"
            : "TapQueue — can't reach server";

    /// <summary>The menu is rebuilt whenever what it shows changes, since tray menus are exported to the desktop.</summary>
    private void BuildMenu()
    {
        var menu = new NativeMenu();

        var status = _connected ? $"Signed in as {_api.Session?.User.DisplayName}"
            : _signedOut ? "Signed out by an admin"
            : $"Not connected{(_lastError is null ? "" : $": {Shorten(_lastError, 60)}")}";
        menu.Add(new NativeMenuItem(status) { IsEnabled = false });
        menu.Add(new NativeMenuItem($"{_heldJobs.Count} held job{(_heldJobs.Count == 1 ? "" : "s")}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());

        var releaseAll = new NativeMenuItem("Release all to") { IsEnabled = _connected && _heldJobs.Count > 0, Menu = [] };
        foreach (var printer in Printers)
        {
            var item = new NativeMenuItem(printer.Online ? printer.Name : $"{printer.Name} (offline)");
            item.Click += async (_, _) => await ReleaseAsync(printer.Id, null);
            releaseAll.Menu.Add(item);
        }
        menu.Add(releaseAll);
        menu.Add(Item("Held jobs…", ShowJobsWindow));
        if (!_connected)
            menu.Add(Item(_signedOut ? "Sign in again" : "Retry connection", () => _ = ConnectAsync()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem($"TapQueue {TapQueueVersion.Current}") { IsEnabled = false });
        menu.Add(new NativeMenuItem(_checkingForUpdates ? "Checking for updates…" : "Check for updates") { IsEnabled = !_checkingForUpdates }
            .WithClick(() => _ = CheckForUpdatesAsync()));
        menu.Add(Item("Exit", Exit));
        _tray.Menu = menu;

        static NativeMenuItem Item(string header, Action onClick) => new NativeMenuItem(header).WithClick(onClick);
    }

    private void ShowJobsWindow()
    {
        if (_jobsWindow is not null)
        {
            _jobsWindow.Activate();
            return;
        }
        _jobsWindow = new JobsWindow(this);
        _jobsWindow.Closed += (_, _) => _jobsWindow = null;
        _jobsWindow.Show();
    }

    public void Notify(string title, string text, bool urgent = false) => _ = Notifications.ShowAsync(title, text, urgent);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void Exit()
    {
        _tray.IsVisible = false;
        _tray.Dispose();
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
        _retryTimer.Stop();
        _api.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

internal static class NativeMenuItemExtensions
{
    public static NativeMenuItem WithClick(this NativeMenuItem item, Action onClick)
    {
        item.Click += (_, _) => onClick();
        return item;
    }
}
