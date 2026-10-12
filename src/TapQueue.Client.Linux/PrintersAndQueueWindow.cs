using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Diagnostics;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Linux;

/// <summary>
/// "View printers and queue": one tab with the queues this person prints to and the printers they
/// can pick up at, one with their own jobs, where they release or delete them.
/// </summary>
public sealed class PrintersAndQueueWindow : Window
{
    private const string JobColumns = "*,310,70,150,80";
    private const string PrinterColumns = "210,*,270,180";

    private readonly TrayApp _app;
    private readonly TabControl _tabs = new();
    private readonly TabItem _queueTab = new() { Header = "My print queue" };

    private readonly StackPanel _queueRows = new();
    private readonly StackPanel _printerRows = new();
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly TextBlock _printersStatus = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly ComboBox _printers = new() { MinWidth = 240 };
    private readonly Button _releaseSelected = new() { Content = "Release selected" };
    private readonly Button _releaseAll = new() { Content = "Release all" };
    private readonly Button _delete = new() { Content = "Delete selected" };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The printers CUPS has, or null until (or if) that could be found out.</summary>
    private HashSet<string>? _installed;
    private bool _checking;
    private bool _showing;
    private string? _shownPrinters;

    /// <param name="showQueue">Open on "My print queue" rather than "Printers".</param>
    public PrintersAndQueueWindow(TrayApp app, bool showQueue)
    {
        _app = app;
        Title = "TapQueue — printers and print queue";
        Icon = app.AppIcon;
        Width = 920;
        Height = 480;
        MinWidth = 620;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var printersPage = new DockPanel();
        var printersBar = Bar(_refresh, _printersStatus);
        DockPanel.SetDock(printersBar, Dock.Bottom);
        printersPage.Children.Add(printersBar);
        printersPage.Children.Add(new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12, 4, 12, 12),
                Children =
                {
                    Heading("Queues you print to"), Row(PrinterColumns, ["Name", "Description", "On this PC", ""], heading: true), _queueRows,
                    Heading("Printers you can pick up at"), Row(PrinterColumns, ["Name", "Location", "Status", "Model"], heading: true), _printerRows,
                },
            },
        });

        _list.ItemTemplate = new FuncDataTemplate<JobDto>((job, _) => job is null ? new TextBlock() : JobRow(job));
        var header = Row(JobColumns, ["Document", "Status", "Pages", "Submitted", "Size"], heading: true, rightAligned: [2, 4]);
        header.Margin = new Thickness(12, 8, 12, 4);
        var queueBar = Bar(new TextBlock { Text = "Printer:", VerticalAlignment = VerticalAlignment.Center },
            _printers, _releaseSelected, _releaseAll, _delete, _status);
        var queuePage = new DockPanel();
        DockPanel.SetDock(queueBar, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        queuePage.Children.Add(queueBar);
        queuePage.Children.Add(header);
        queuePage.Children.Add(_list);

        _queueTab.Content = queuePage;
        _tabs.Items.Add(new TabItem { Header = "Printers", Content = printersPage });
        _tabs.Items.Add(_queueTab);
        if (showQueue)
            _tabs.SelectedItem = _queueTab;
        Content = _tabs;

        _refresh.Click += async (_, _) => await ReloadAsync();
        _releaseSelected.Click += async (_, _) => await ReleaseAsync(Selected().Where(PrintQueue.IsHeld).Select(j => j.Id).ToList());
        _releaseAll.Click += async (_, _) => await ReleaseAsync(null);
        _delete.Click += async (_, _) => await DeleteAsync();
        _list.SelectionChanged += (_, _) =>
        {
            if (!_showing) UpdateButtons();
        };
        _printers.SelectionChanged += (_, _) =>
        {
            if (!_showing) UpdateButtons();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        RefreshView();
        Opened += async (_, _) => await ReloadAsync();
    }

    public void ShowQueue() => _tabs.SelectedItem = _queueTab;

    /// <summary>Asks the server (and CUPS) again, then shows what they said.</summary>
    private async Task ReloadAsync()
    {
        _refresh.IsEnabled = false;
        _checking = true;
        ShowPrinters();
        try
        {
            var installed = InstalledPrintersAsync();
            await _app.RefreshAsync();
            _installed = await installed;
        }
        finally
        {
            _checking = false;
            _refresh.IsEnabled = true;
            RefreshView();
        }
    }

    /// <summary>Shows what the tray app knows now. It calls this whenever that changes.</summary>
    public void RefreshView()
    {
        ShowPrinters();
        ShowJobs();
    }

    private void ShowPrinters()
    {
        _printersStatus.Text = _checking ? "Checking…"
            : !_app.Connected ? "Not connected to your TapQueue server."
            : _app.Queues.Count == 0 && _app.Printers.Count == 0 ? "Your TapQueue server has no printers for you."
            : "";

        var queues = _app.Queues
            .Select(q => new[] { q.Name, q.Description, PrintQueue.QueueStatus(_installed, CupsPrinterInstaller.CupsName(q.Name)), "" }).ToList();
        var printers = _app.Printers.Select(p => new[] { p.Name, p.Location, PrintQueue.PrinterStatus(p), p.MakeAndModel ?? "" }).ToList();
        // This runs every few seconds; leave the lists alone (an open drop-down) unless something changed.
        var shown = string.Join('\n', queues.Concat(printers).Select(row => string.Join('\t', row)).Concat(_app.Printers.Select(p => p.Id)));
        if (shown == _shownPrinters) return;
        _shownPrinters = shown;

        Fill(_queueRows, queues, "None.");
        Fill(_printerRows, printers, "None.");

        var chosen = (_printers.SelectedItem as PrinterItem)?.Printer.Id;
        var items = _app.Printers.Select(p => new PrinterItem(p)).ToList();
        _showing = true;
        _printers.ItemsSource = items;
        _printers.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, items.FindIndex(p => p.Printer.Id == chosen));
        _showing = false;
        UpdateButtons();

        static void Fill(StackPanel panel, List<string[]> rows, string none)
        {
            panel.Children.Clear();
            foreach (var cells in rows)
            {
                var row = Row(PrinterColumns, cells);
                foreach (var cell in row.Children.OfType<TextBlock>().Where(c => !string.IsNullOrEmpty(c.Text)))
                    ToolTip.SetTip(cell, cell.Text); // for what the column cuts off
                panel.Children.Add(row);
            }
            if (rows.Count == 0)
                panel.Children.Add(new TextBlock { Text = none, Opacity = 0.6, Margin = new Thickness(0, 3) });
        }
    }

    private void ShowJobs()
    {
        var selected = Selected().Select(j => j.Id).ToHashSet();
        _showing = true;
        _list.ItemsSource = _app.Jobs.ToList();
        foreach (var job in _app.Jobs.Where(j => selected.Contains(j.Id)))
            _list.SelectedItems!.Add(job);
        _showing = false;
        _status.Text = _app.Jobs.Count == 0 ? "Nothing waiting, and nothing printed in the last day." : "";
        UpdateButtons();
    }

    private Grid JobRow(JobDto job)
    {
        var row = Row(JobColumns,
            [job.Name, PrintQueue.Status(job, _app.Printers), PrintQueue.Pages(job), job.SubmittedAt.ToLocalTime().ToString("g"), PrintQueue.Size(job.SizeBytes)],
            rightAligned: [2, 4]);
        if (!PrintQueue.IsHeld(job))
            row.Opacity = 0.6;
        if (job.Error is not null)
            ToolTip.SetTip(row, "Last attempt failed: " + job.Error);
        return row;
    }

    /// <summary>One line of a table: a job, a printer, or the column headings.</summary>
    private static Grid Row(string columns, string[] cells, bool heading = false, int[]? rightAligned = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(0, 3) };
        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                Text = cells[i],
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = rightAligned?.Contains(i) == true ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    private static WrapPanel Bar(params Control[] controls)
    {
        var bar = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        foreach (var control in controls)
        {
            control.Margin = new Thickness(4);
            bar.Children.Add(control);
        }
        return bar;
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
        if (!await AskAsync(PrintQueue.DeleteQuestion(jobs), jobs.All(PrintQueue.IsHeld) ? "Delete" : "Go ahead", "Keep")) return;
        List<string> notes = [];
        await BusyAsync(jobs.All(PrintQueue.IsHeld) ? "Deleting…" : "Asking the printer to stop…", async () => notes = await _app.DeleteAsync(jobs));
        if (notes.Count > 0)
            await AskAsync(string.Join("\n\n", notes), "OK", null);
    }

    private async Task BusyAsync(string status, Func<Task> work)
    {
        IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        _status.Text = status;
        try
        {
            await work();
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursor.Default;
            RefreshView();
        }
    }

    private List<JobDto> Selected() => _list.SelectedItems?.OfType<JobDto>().ToList() ?? [];

    private void UpdateButtons()
    {
        var hasPrinter = _printers.SelectedItem is not null;
        var selected = Selected();
        _releaseSelected.IsEnabled = hasPrinter && selected.Any(PrintQueue.IsHeld);
        _releaseAll.IsEnabled = hasPrinter && _app.HeldJobs.Count > 0;
        _delete.IsEnabled = selected.Any(PrintQueue.CanDelete);
    }

    /// <summary>A question (two buttons) or something to read (one).</summary>
    private async Task<bool> AskAsync(string text, string yesLabel, string? noLabel)
    {
        var yes = new Button { Content = yesLabel, IsDefault = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
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
                Children = { new TextBlock { Text = text, MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, buttons },
            },
        };
        if (noLabel is not null)
        {
            var no = new Button { Content = noLabel, IsCancel = true };
            no.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(no);
        }
        buttons.Children.Add(yes);
        yes.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>What <c>lpstat -e</c> lists: every printer CUPS knows on this PC.</summary>
    private static async Task<HashSet<string>?> InstalledPrintersAsync()
    {
        try
        {
            var start = new ProcessStartInfo("lpstat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-e");
            start.Environment["LC_ALL"] = "C";
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode != 0 ? null
                : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return null; // no CUPS command-line tools, or CUPS isn't answering
        }
    }

    private sealed record PrinterItem(PrinterDto Printer)
    {
        public override string ToString() => Printer.Online ? Printer.Name : $"{Printer.Name} (offline)";
    }
}
