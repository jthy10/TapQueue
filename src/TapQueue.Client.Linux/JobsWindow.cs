using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Linux;

/// <summary>Lists the user's held jobs and lets them release or cancel them.</summary>
public sealed class JobsWindow : Window
{
    private const string Columns = "*,150,80,150";

    private readonly TrayApp _app;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ComboBox _printers = new() { MinWidth = 240 };
    private readonly Button _releaseSelected = new() { Content = "Release selected" };
    private readonly Button _releaseAll = new() { Content = "Release all" };
    private readonly Button _cancel = new() { Content = "Cancel selected" };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _refreshing;

    public JobsWindow(TrayApp app)
    {
        _app = app;
        Title = "TapQueue — held print jobs";
        Icon = app.AppIcon;
        Width = 760;
        Height = 420;
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _list.ItemTemplate = new FuncDataTemplate<JobDto>((job, _) => job is null ? new TextBlock() : Row(job));
        var header = Row(null);
        header.Margin = new Thickness(12, 8, 12, 4);

        var bar = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        foreach (var control in new Control[]
                 {
                     new TextBlock { Text = "Printer:", VerticalAlignment = VerticalAlignment.Center }, _printers,
                     _releaseSelected, _releaseAll, _cancel, _status,
                 })
        {
            control.Margin = new Thickness(4);
            bar.Children.Add(control);
        }

        var layout = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(bar);
        layout.Children.Add(header);
        layout.Children.Add(_list);
        Content = layout;

        _releaseSelected.Click += async (_, _) => await ReleaseAsync(SelectedIds());
        _releaseAll.Click += async (_, _) => await ReleaseAsync(null);
        _cancel.Click += async (_, _) =>
        {
            var ids = SelectedIds();
            if (ids.Count == 0) return;
            if (!await ConfirmAsync($"Delete {ids.Count} held job(s)? They won't be printed.")) return;
            await _app.CancelAsync(ids);
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (!_refreshing) UpdateButtons();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        _printers.ItemsSource = _app.Printers.Select(p => new PrinterItem(p)).ToList();
        if (_app.Printers.Count > 0)
            _printers.SelectedIndex = 0;
        _printers.SelectionChanged += (_, _) => UpdateButtons();

        RefreshJobs();
        Opened += async (_, _) => await _app.RefreshAsync();
    }

    public void RefreshJobs()
    {
        var selected = SelectedIds().ToHashSet();
        _refreshing = true;
        _list.ItemsSource = _app.HeldJobs.ToList();
        foreach (var job in _app.HeldJobs.Where(j => selected.Contains(j.Id)))
            _list.SelectedItems!.Add(job);
        _refreshing = false;
        _status.Text = _app.HeldJobs.Count == 0 ? "No held jobs." : "";
        UpdateButtons();
    }

    /// <summary>A job's row, or the column headings when <paramref name="job"/> is null.</summary>
    private static Grid Row(JobDto? job)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        string[] cells = job is null
            ? ["Document", "Submitted", "Size", "Deleted after"]
            : [job.Name, job.SubmittedAt.ToLocalTime().ToString("g"), FormatSize(job.SizeBytes), job.ExpiresAt.ToLocalTime().ToString("g")];
        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = i == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FontWeight = job is null ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        if (job?.Error is not null)
            ToolTip.SetTip(grid, "Last attempt failed: " + job.Error);
        return grid;
    }

    private async Task ReleaseAsync(IReadOnlyList<long>? ids)
    {
        if (_printers.SelectedItem is not PrinterItem printer || ids is { Count: 0 }) return;
        IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        _status.Text = $"Sending to {printer.Printer.Name}…";
        try
        {
            await _app.ReleaseAsync(printer.Printer.Id, ids);
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursor.Default;
            RefreshJobs();
        }
    }

    private List<long> SelectedIds() => _list.SelectedItems?.OfType<JobDto>().Select(j => j.Id).ToList() ?? [];

    private void UpdateButtons()
    {
        var hasPrinter = _printers.SelectedItem is not null;
        _releaseSelected.IsEnabled = hasPrinter && SelectedIds().Count > 0;
        _releaseAll.IsEnabled = hasPrinter && _app.HeldJobs.Count > 0;
        _cancel.IsEnabled = SelectedIds().Count > 0;
    }

    private async Task<bool> ConfirmAsync(string question)
    {
        var yes = new Button { Content = "Delete", IsDefault = true };
        var no = new Button { Content = "Keep", IsCancel = true };
        var dialog = new Window
        {
            Title = "TapQueue",
            Icon = Icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = question, MaxWidth = 360, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
                },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private static string FormatSize(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private sealed record PrinterItem(PrinterDto Printer)
    {
        public override string ToString() => Printer.Online ? Printer.Name : $"{Printer.Name} (offline)";
    }
}
