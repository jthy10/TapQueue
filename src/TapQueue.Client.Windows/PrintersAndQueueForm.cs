using System.Drawing.Printing;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// "View printers and queue": one tab with the queues this person prints to and the printers they
/// can pick up at, one with their own jobs, where they release or delete them.
/// </summary>
public sealed class PrintersAndQueueForm : Form
{
    private readonly TrayApp _app;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _printersTab = new("Printers");
    private readonly TabPage _queueTab = new("My print queue");

    private readonly ListView _printerList = NewList();
    private readonly ListViewGroup _queueGroup = new("Queues you print to");
    private readonly ListViewGroup _printerGroup = new("Printers you can pick up at");
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _printersStatus = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    private readonly ListView _jobList = NewList();
    private readonly ComboBox _printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button _releaseSelected = new() { Text = "Release selected", AutoSize = true };
    private readonly Button _releaseAll = new() { Text = "Release all", AutoSize = true };
    private readonly Button _delete = new() { Text = "Delete selected", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    /// <summary>The printers Windows has, or null until (or if) that could be found out.</summary>
    private HashSet<string>? _installed;
    private bool _checking;
    private string? _shownPrinters;

    /// <param name="showQueue">Open on "My print queue" rather than "Printers".</param>
    public PrintersAndQueueForm(TrayApp app, bool showQueue)
    {
        _app = app;
        Text = "TapQueue — printers and print queue";
        Icon = TrayIcon.Create(connected: true);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 460);
        MinimumSize = new Size(600, 320);

        _printerList.Columns.Add("Name", 220);
        _printerList.Columns.Add("Description or location", 240);
        _printerList.Columns.Add("Status", 190);
        _printerList.Columns.Add("Model", 130);
        _printerList.Groups.AddRange([_queueGroup, _printerGroup]);
        _printerList.MultiSelect = false;
        var printersBar = Bar(_refresh, _printersStatus);
        _printersTab.Controls.Add(_printerList);
        _printersTab.Controls.Add(printersBar);

        _jobList.Columns.Add("Document", 270);
        _jobList.Columns.Add("Status", 230);
        _jobList.Columns.Add("Pages", 60, HorizontalAlignment.Right);
        _jobList.Columns.Add("Submitted", 130);
        _jobList.Columns.Add("Size", 70, HorizontalAlignment.Right);
        _jobList.ShowItemToolTips = true;
        var queueBar = Bar(new Label { Text = "Printer:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _printers, _releaseSelected, _releaseAll, _delete, _status);
        _queueTab.Controls.Add(_jobList);
        _queueTab.Controls.Add(queueBar);

        _tabs.TabPages.AddRange([_printersTab, _queueTab]);
        if (showQueue)
            _tabs.SelectedTab = _queueTab;
        Controls.Add(_tabs);

        _refresh.Click += async (_, _) => await ReloadAsync();
        _releaseSelected.Click += async (_, _) => await ReleaseAsync(Selected().Where(PrintQueue.IsHeld).Select(j => j.Id).ToList());
        _releaseAll.Click += async (_, _) => await ReleaseAsync(null);
        _delete.Click += async (_, _) => await DeleteAsync();
        _jobList.SelectedIndexChanged += (_, _) => UpdateButtons();
        _printers.SelectedIndexChanged += (_, _) => UpdateButtons();

        RefreshView();
        Shown += async (_, _) => await ReloadAsync();
    }

    public void ShowQueue() => _tabs.SelectedTab = _queueTab;

    /// <summary>Asks the server (and Windows) again, then shows what they said.</summary>
    private async Task ReloadAsync()
    {
        _refresh.Enabled = false;
        _checking = true;
        ShowPrinters();
        try
        {
            var installed = Task.Run(InstalledPrinters);
            await _app.RefreshAsync();
            _installed = await installed;
        }
        finally
        {
            _checking = false;
            if (!IsDisposed)
            {
                _refresh.Enabled = true;
                RefreshView();
            }
        }
    }

    /// <summary>Shows what the tray app knows now. It calls this whenever that changes.</summary>
    public void RefreshView()
    {
        if (IsDisposed) return;
        ShowPrinters();
        ShowJobs();
    }

    private void ShowPrinters()
    {
        _printersStatus.Text = _checking ? "Checking…"
            : !_app.Connected ? "Not connected to your TapQueue server."
            : _app.Queues.Count == 0 && _app.Printers.Count == 0 ? "Your TapQueue server has no printers for you."
            : "";

        var queues = _app.Queues.Select(q => new[] { q.Name, q.Description, PrintQueue.QueueStatus(_installed, q.Name), "" }).ToList();
        var printers = _app.Printers.Select(p => new[] { p.Name, p.Location, PrintQueue.PrinterStatus(p), p.MakeAndModel ?? "" }).ToList();
        // This runs every few seconds; leave the lists alone (selection, an open drop-down) unless something changed.
        var shown = string.Join('\n', queues.Concat(printers).Select(row => string.Join('\t', row)).Concat(_app.Printers.Select(p => p.Id)));
        if (shown == _shownPrinters) return;
        _shownPrinters = shown;

        _printerList.BeginUpdate();
        _printerList.Items.Clear();
        _printerList.Items.AddRange([.. queues.Select(row => new ListViewItem(row, _queueGroup))]);
        _printerList.Items.AddRange([.. printers.Select(row => new ListViewItem(row, _printerGroup))]);
        _printerList.EndUpdate();

        var chosen = (_printers.SelectedItem as PrinterItem)?.Printer.Id;
        _printers.BeginUpdate();
        _printers.Items.Clear();
        foreach (var printer in _app.Printers)
            _printers.Items.Add(new PrinterItem(printer));
        _printers.SelectedIndex = _printers.Items.Count == 0 ? -1
            : Math.Max(0, _printers.Items.Cast<PrinterItem>().ToList().FindIndex(p => p.Printer.Id == chosen));
        _printers.EndUpdate();
    }

    private void ShowJobs()
    {
        var selected = Selected().Select(j => j.Id).ToHashSet();
        _jobList.BeginUpdate();
        _jobList.Items.Clear();
        foreach (var job in _app.Jobs)
        {
            var item = new ListViewItem([
                job.Name,
                PrintQueue.Status(job, _app.Printers),
                PrintQueue.Pages(job),
                job.SubmittedAt.ToLocalTime().ToString("g"),
                PrintQueue.Size(job.SizeBytes),
            ]) { Tag = job, Selected = selected.Contains(job.Id) };
            if (job.Error is not null)
                item.ToolTipText = "Last attempt failed: " + job.Error;
            if (!PrintQueue.IsHeld(job))
                item.ForeColor = SystemColors.GrayText;
            _jobList.Items.Add(item);
        }
        _jobList.EndUpdate();
        _status.Text = _app.Jobs.Count == 0 ? "Nothing waiting, and nothing printed in the last day." : "";
        UpdateButtons();
    }

    private async Task ReleaseAsync(IReadOnlyList<long>? ids)
    {
        if (_printers.SelectedItem is not PrinterItem printer || ids is { Count: 0 }) return;
        await BusyAsync($"Sending to {printer.Printer.Name}…", () => _app.ReleaseAsync(printer.Printer.Id, ids));
    }

    private async Task DeleteAsync()
    {
        var jobs = Selected().Where(PrintQueue.CanDelete).ToList();
        if (jobs.Count == 0) return;
        if (MessageBox.Show(this, PrintQueue.DeleteQuestion(jobs), "TapQueue", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        List<string> notes = [];
        await BusyAsync(jobs.All(PrintQueue.IsHeld) ? "Deleting…" : "Asking the printer to stop…", async () => notes = await _app.DeleteAsync(jobs));
        if (notes.Count > 0 && !IsDisposed)
            MessageBox.Show(this, string.Join("\n\n", notes), "TapQueue", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task BusyAsync(string status, Func<Task> work)
    {
        UseWaitCursor = true;
        Enabled = false;
        _status.Text = status;
        try
        {
            await work();
        }
        finally
        {
            if (!IsDisposed)
            {
                Enabled = true;
                UseWaitCursor = false;
                RefreshView();
            }
        }
    }

    private List<JobDto> Selected() => _jobList.SelectedItems.Cast<ListViewItem>().Select(i => (JobDto)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var hasPrinter = _printers.SelectedItem is not null;
        var selected = Selected();
        _releaseSelected.Enabled = hasPrinter && selected.Any(PrintQueue.IsHeld);
        _releaseAll.Enabled = hasPrinter && _app.HeldJobs.Count > 0;
        _delete.Enabled = selected.Any(PrintQueue.CanDelete);
    }

    private static HashSet<string>? InstalledPrinters()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // the print spooler isn't running
        }
    }

    private static ListView NewList() => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
    };

    private static FlowLayoutPanel Bar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        bar.Controls.AddRange(controls);
        return bar;
    }

    private sealed record PrinterItem(PrinterDto Printer)
    {
        public override string ToString() => Printer.Online ? Printer.Name : $"{Printer.Name} (offline)";
    }
}
