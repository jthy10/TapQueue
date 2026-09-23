using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>Lists the user's held jobs and lets them release or cancel them.</summary>
public sealed class JobsForm : Form
{
    private readonly TrayApp _app;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = false,
        HideSelection = false,
    };
    private readonly ComboBox _printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _releaseSelected = new() { Text = "Release selected", AutoSize = true };
    private readonly Button _releaseAll = new() { Text = "Release all", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel selected", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    public JobsForm(TrayApp app)
    {
        _app = app;
        Text = "TapQueue — held print jobs";
        Icon = TrayIcon.Create(connected: true);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 420);
        MinimumSize = new Size(560, 300);

        _list.Columns.Add("Document", 300);
        _list.Columns.Add("Submitted", 140);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Deleted after", 140);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        bar.Controls.AddRange([new Label { Text = "Printer:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _printers, _releaseSelected, _releaseAll, _cancel, _status]);
        Controls.Add(_list);
        Controls.Add(bar);

        _releaseSelected.Click += async (_, _) => await ReleaseAsync(SelectedIds());
        _releaseAll.Click += async (_, _) => await ReleaseAsync(null);
        _cancel.Click += async (_, _) =>
        {
            var ids = SelectedIds();
            if (ids.Count == 0) return;
            if (MessageBox.Show(this, $"Delete {ids.Count} held job(s)? They won't be printed.", "TapQueue",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await _app.CancelAsync(ids);
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        foreach (var printer in _app.Printers)
            _printers.Items.Add(new PrinterItem(printer));
        if (_printers.Items.Count > 0)
            _printers.SelectedIndex = 0;

        RefreshJobs();
        Shown += async (_, _) => await _app.RefreshAsync();
    }

    public void RefreshJobs()
    {
        var selected = SelectedIds().ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var job in _app.HeldJobs)
        {
            var item = new ListViewItem([
                job.Name,
                job.SubmittedAt.ToLocalTime().ToString("g"),
                FormatSize(job.SizeBytes),
                job.ExpiresAt.ToLocalTime().ToString("g"),
            ]) { Tag = job.Id, Selected = selected.Contains(job.Id) };
            if (job.Error is not null)
                item.ToolTipText = "Last attempt failed: " + job.Error;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _status.Text = _app.HeldJobs.Count == 0 ? "No held jobs." : "";
        UpdateButtons();
    }

    private async Task ReleaseAsync(IReadOnlyList<long>? ids)
    {
        if (_printers.SelectedItem is not PrinterItem printer || ids is { Count: 0 }) return;
        UseWaitCursor = true;
        Enabled = false;
        _status.Text = $"Sending to {printer.Printer.Name}…";
        try
        {
            await _app.ReleaseAsync(printer.Printer.Id, ids);
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
            RefreshJobs();
        }
    }

    private List<long> SelectedIds() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (long)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var hasPrinter = _printers.SelectedItem is not null;
        _releaseSelected.Enabled = hasPrinter && _list.SelectedItems.Count > 0;
        _releaseAll.Enabled = hasPrinter && _list.Items.Count > 0;
        _cancel.Enabled = _list.SelectedItems.Count > 0;
    }

    private static string FormatSize(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private sealed record PrinterItem(PrinterDto Printer)
    {
        public override string ToString() => Printer.Online ? Printer.Name : $"{Printer.Name} (offline)";
    }
}
