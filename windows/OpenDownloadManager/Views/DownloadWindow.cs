using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.Foundation;

namespace OpenDownloadManager.Views;

/// <summary>
/// One download's window, modelled on the Windows file copy dialog: "54% complete", a speed graph
/// drawn across the progress, name, time and size remaining, and "More details" for the
/// connections. Fixed size; closing it does not stop the download.
/// </summary>
public sealed class DownloadWindow : Window
{
    private const int WindowWidth = 520;
    private const int CollapsedHeight = 388;
    private const int ExpandedHeight = 612;

    private readonly CoreClient _core;
    private readonly DownloadItem _item;
    private readonly DispatcherQueueTimer _timer;

    private readonly TextBlock _route = Text(caption: true, secondary: true);
    private readonly TextBlock _headline = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = IconButton("", "Pause");
    private readonly Button _cancel = IconButton("", "Cancel download");
    private readonly Canvas _graph = new();
    private readonly Border _graphFrame;
    private readonly TextBlock _speedLabel = Text(caption: true);
    private readonly Grid _facts = new() { ColumnSpacing = 12, RowSpacing = 4 };
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly Button _detailsToggle;
    private readonly FontIcon _detailsChevron = new() { Glyph = "", FontSize = 10 };
    private readonly TextBlock _detailsLabel = new() { Text = "More details" };
    private readonly StackPanel _details = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Grid _map = new() { Height = 8 };
    private readonly StackPanel _parts = new() { Spacing = 10 };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _open = new() { Content = "Open" };
    private readonly Button _folder = new() { Content = "Show in folder" };
    private readonly Button _close = new() { Content = "Close" };

    /// <summary>Speed samples against progress (0 to 1), for the graph.</summary>
    private readonly List<(double At, double Speed)> _samples = [];
    private JsonArray? _segments;
    private long _total;
    private bool _polling;
    private bool _expanded;

    public ulong Id => _item.Id;

    public DownloadWindow(CoreClient core, DownloadItem item)
    {
        _core = core;
        _item = item;
        SystemBackdrop = new MicaBackdrop();

        // ----- header: route, then "54% complete" with pause and cancel -----
        var headline = new Grid { ColumnSpacing = 4 };
        headline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headline.Children.Add(_headline);
        Grid.SetColumn(_pause, 1);
        Grid.SetColumn(_cancel, 2);
        headline.Children.Add(_pause);
        headline.Children.Add(_cancel);

        // ----- the speed graph -----
        _graphFrame = new Border
        {
            Height = 92,
            BorderThickness = new Thickness(1),
            BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"),
            Background = Res<Brush>("CardBackgroundFillColorSecondaryBrush"),
            Child = _graph,
        };
        _graphFrame.SizeChanged += (_, _) => DrawGraph();
        _speedLabel.HorizontalAlignment = HorizontalAlignment.Right;

        // ----- facts, label column then value column -----
        _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var label in new[] { "Name:", "Time remaining:", "Size remaining:" }) AddFact(label);

        // ----- more details: the connection map and each connection, in a fixed scrolling area -----
        _detailsToggle = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _detailsChevron, _detailsLabel } },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 8, 4),
        };
        _detailsToggle.Click += (_, _) => SetExpanded(!_expanded);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_detailsToggle, "More details");
        var mapFrame = new Border { CornerRadius = new CornerRadius(2), Background = Res<Brush>("ControlStrokeColorDefaultBrush"), Child = _map };
        ToolTipService.SetToolTip(mapFrame, "What each connection has fetched, across the whole file");
        _details.Children.Add(new Grid
        {
            Children =
            {
                Text(caption: true, secondary: true, text: "Connections"),
            },
        });
        _details.Children.Add(mapFrame);
        _details.Children.Add(new Border
        {
            Height = 168,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"),
            Background = Res<Brush>("CardBackgroundFillColorSecondaryBrush"),
            Child = new ScrollViewer { Content = _parts, Padding = new Thickness(12, 10, 16, 10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });

        // ----- footer: details toggle on the left, buttons on the right -----
        _open.Style = Res<Style>("AccentButtonStyle");
        _open.Click += (_, _) => { if (_item.FilePath is { } p) Shell.Open(p); Close(); };
        _folder.Click += (_, _) => Shell.ShowInFolder(_item.FilePath, _item.SaveDir);
        _close.Click += (_, _) => Close();
        _pause.Click += async (_, _) => await TogglePause();
        _cancel.Click += async (_, _) => await CancelDownload();
        var footer = new Grid();
        footer.Children.Add(_detailsToggle);
        footer.Children.Add(_buttons);

        var body = new Grid { Padding = new Thickness(20, 12, 20, 16), RowSpacing = 12 };
        for (var i = 0; i < 6; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        void Row(FrameworkElement e, int r) { Grid.SetRow(e, r); body.Children.Add(e); }
        Row(_route, 0);
        Row(headline, 1);
        Row(new StackPanel { Spacing = 4, Children = { _speedLabel, _graphFrame } }, 2);
        Row(_facts, 3);
        Row(footer, 4);
        Row(_details, 5);
        Content = body;

        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, CollapsedHeight));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
        }
        ViewSettings.Current.Track(this);

        _item.PropertyChanged += OnItemChanged;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Tick += async (_, _) => await Poll();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _item.PropertyChanged -= OnItemChanged;
        };
        Refresh();
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static TextBlock Text(bool caption = false, bool secondary = false, string text = "") => new()
    {
        Text = text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        Style = caption ? Res<Style>("CaptionTextBlockStyle") : Res<Style>("BodyTextBlockStyle"),
        Foreground = secondary ? Res<Brush>("TextFillColorSecondaryBrush") : Res<Brush>("TextFillColorPrimaryBrush"),
    };

    /// <summary>A small subtle icon button, like the pause and stop buttons of the copy dialog.</summary>
    private static Button IconButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties(b, tip);
        return b;
    }

    private static void AutomationProperties(Button b, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);

    private void AddFact(string label)
    {
        var row = _facts.RowDefinitions.Count;
        _facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Text(secondary: true, text: label);
        var v = Text();
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        _facts.Children.Add(l);
        _facts.Children.Add(v);
        _values[label] = v;
        _labels[label] = l;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update() raises many properties at once; refresh on the last one it raises.
        if (e.PropertyName == nameof(DownloadItem.StateGlyph)) Refresh();
    }

    private void Refresh()
    {
        var i = _item;
        var done = i.IsCompleted;
        var folder = string.IsNullOrEmpty(i.SaveDir) ? "" : System.IO.Path.GetFileName(i.SaveDir.TrimEnd('\\', '/'));
        var host = Uri.TryCreate(i.Url, UriKind.Absolute, out var u) ? u.Host : "";
        _route.Text = done ? $"Downloaded from {host} to {folder}" : $"Downloading from {host} to {folder}";
        ToolTipService.SetToolTip(_route, $"{i.Url}\n{i.FilePath ?? i.SaveDir}");

        var percent = i.TotalSize is > 0 || done ? $"{i.Progress:0}% complete" : $"{Format.Bytes(i.Downloaded)} downloaded";
        _headline.Text = done ? "Download complete"
            : i.IsFailed ? (i.LinkExpired ? "Link expired" : "Download failed")
            : i.State == "paused" ? $"Paused – {percent}"
            : i.State is "queued" ? "Waiting in the queue"
            : i.State == "connecting" ? "Connecting…"
            : percent;
        Title = done ? $"Done – {i.DisplayName}"
            : i.IsFailed ? $"Failed – {i.DisplayName}"
            : $"{(i.TotalSize is > 0 ? $"{i.Progress:0}% complete" : "Downloading")} – {i.DisplayName}";

        _values["Name:"].Text = i.DisplayName;
        ToolTipService.SetToolTip(_values["Name:"], i.DisplayName);
        _values["Time remaining:"].Text = done ? "None" : i.IsFailed ? "—" : i.TimeLeftText is { Length: > 0 } t ? $"About {t}" : "Calculating…";
        _values["Size remaining:"].Text = i.TotalSize is > 0
            ? $"{Format.Bytes(Math.Max(0, i.TotalSize.Value - i.Downloaded))} ({Format.Bytes(i.TotalSize.Value)})"
            : i.Downloaded > 0 ? $"Unknown ({Format.Bytes(i.Downloaded)} so far)" : "Unknown";
        // A failed download shows what went wrong where the time would be.
        var failed = i.IsFailed && i.Error is not null;
        _labels["Time remaining:"].Text = failed ? "Problem:" : "Time remaining:";
        _values["Time remaining:"].Foreground = failed ? i.StatusBrush : Res<Brush>("TextFillColorPrimaryBrush");
        if (failed) _values["Time remaining:"].Text = i.Error!;
        ToolTipService.SetToolTip(_values["Time remaining:"], failed ? i.Error : null);

        _pause.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        ((FontIcon)_pause.Content).Glyph = i.CanResume ? "" : "";
        ToolTipService.SetToolTip(_pause, i.CanResume ? "Resume" : "Pause");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_pause, i.CanResume ? "Resume" : "Pause");

        _buttons.Children.Clear();
        if (done)
        {
            _buttons.Children.Add(_open);
            _buttons.Children.Add(_folder);
        }
        _buttons.Children.Add(_close);
        _close.Content = done ? "Close" : "Hide";
        ToolTipService.SetToolTip(_close, done ? null : "Close this window; the download carries on");
        _detailsToggle.Visibility = i.Kind == "video" ? Visibility.Collapsed : Visibility.Visible;

        Sample();
        DrawGraph();
        if (done)
        {
            _timer?.Stop();
            MarkPartsDone();
        }
    }

    // ----- the speed graph -----

    /// <summary>Records the current speed against how far the download has got.</summary>
    private void Sample()
    {
        var i = _item;
        if (i.State != "downloading" || i.Speed <= 0) return;
        double at = i.TotalSize is > 0 ? i.Downloaded / (double)i.TotalSize.Value : Math.Min(0.98, _samples.Count / 300.0);
        if (_samples.Count > 0 && at < _samples[^1].At) _samples.Clear(); // the download restarted
        _samples.Add((at, i.Speed));
        if (_samples.Count > 600) _samples.RemoveAt(0);
    }

    /// <summary>
    /// Speed as a filled line across the progress, like the file copy dialog: the left part is
    /// shaded up to how far the download has got, and a line marks the current speed.
    /// </summary>
    private void DrawGraph()
    {
        _graph.Children.Clear();
        double w = _graphFrame.ActualWidth - 2, h = _graphFrame.ActualHeight - 2;
        if (w <= 0 || h <= 0) return;
        var accent = Res<SolidColorBrush>("AccentFillColorDefaultBrush").Color;
        var done = _item.IsCompleted;
        var progress = done ? 1 : _item.TotalSize is > 0 ? _item.Downloaded / (double)_item.TotalSize.Value : _samples.Count > 0 ? _samples[^1].At : 0;

        // Faint grid lines, as in the copy dialog.
        for (var k = 1; k < 4; k++)
            _graph.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * k / 4, Y2 = h * k / 4, Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"), StrokeThickness = 1 });

        // The part already downloaded, shaded.
        _graph.Children.Add(new Rectangle { Width = Math.Max(0, w * progress), Height = h, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(28, accent.R, accent.G, accent.B)) });

        var speedNow = _item.State == "downloading" ? _item.Speed : 0;
        // A space keeps the label's height, so nothing below it moves.
        _speedLabel.Text = speedNow > 0 ? $"Speed: {Format.Speed(speedNow)}" : " ";
        if (_samples.Count < 2) return;

        var top = Math.Max(_samples.Max(s => s.Speed), speedNow) * 1.25;
        Point P((double At, double Speed) s) => new(s.At * w, h - s.Speed / top * h);

        var area = new PathFigure { StartPoint = new Point(_samples[0].At * w, h), IsClosed = true };
        foreach (var s in _samples) area.Segments.Add(new LineSegment { Point = P(s) });
        area.Segments.Add(new LineSegment { Point = new Point(_samples[^1].At * w, h) });
        _graph.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { area } },
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B)),
        });

        var line = new Polyline { Stroke = new SolidColorBrush(accent), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        foreach (var s in _samples) line.Points.Add(P(s));
        _graph.Children.Add(line);

        if (speedNow > 0)
        {
            var y = h - speedNow / top * h;
            _graph.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Res<Brush>("TextFillColorSecondaryBrush"), StrokeThickness = 1, Opacity = 0.6 });
        }
    }

    // ----- more details -----

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _detailsChevron.Glyph = expanded ? "" : "";
        _detailsLabel.Text = expanded ? "Fewer details" : "More details";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_detailsToggle, _detailsLabel.Text);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, expanded ? ExpandedHeight : CollapsedHeight));
        if (expanded)
        {
            _ = Poll();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async Task Poll()
    {
        if (_polling || !_expanded) return;
        _polling = true;
        try
        {
            var reply = await _core.CallAsync(new { type = "details", id = _item.Id });
            if (reply["ok"]?.GetValue<bool>() != true) return;
            _segments = reply["segments"] as JsonArray;
            _total = reply["total"] is JsonValue t && t.TryGetValue<long>(out var n) ? n : 0;
            DrawMap();
            DrawParts();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>The whole file as a thin bar; each connection's fetched part filled, at true scale.</summary>
    private void DrawMap()
    {
        _map.Children.Clear();
        _map.ColumnDefinitions.Clear();
        if (_segments is null || _total <= 0) return;
        var accent = Res<Brush>("AccentFillColorDefaultBrush");
        var empty = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // Each segment becomes two proportional columns: fetched and still to fetch.
        var col = 0;
        foreach (var s in _segments.OfType<JsonObject>().OrderBy(s => s["start"]!.GetValue<long>()))
        {
            long start = s["start"]!.GetValue<long>(), cursor = s["cursor"]!.GetValue<long>(), end = s["end"]!.GetValue<long>();
            foreach (var (len, brush) in new[] { (cursor - start, accent), (end - cursor, (Brush)empty) })
            {
                if (len <= 0) continue;
                _map.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(len, GridUnitType.Star) });
                var r = new Rectangle { Fill = brush };
                Grid.SetColumn(r, col++);
                _map.Children.Add(r);
            }
        }
    }

    /// <summary>One line per connection's range: a thin bar and how much of it is fetched.</summary>
    private void DrawParts()
    {
        var segs = _segments?.OfType<JsonObject>().OrderBy(s => s["start"]!.GetValue<long>()).ToList() ?? [];
        while (_parts.Children.Count > segs.Count) _parts.Children.RemoveAt(_parts.Children.Count - 1);
        if (segs.Count == 0 && _parts.Children.Count == 0)
            _parts.Children.Add(Text(caption: true, secondary: true, text: "Shown while the download runs."));
        for (var k = 0; k < segs.Count; k++)
        {
            long start = segs[k]["start"]!.GetValue<long>(), cursor = segs[k]["cursor"]!.GetValue<long>(), end = segs[k]["end"]!.GetValue<long>();
            var size = Math.Max(1, end - start);
            if (k >= _parts.Children.Count || _parts.Children[k] is not Grid) { if (k < _parts.Children.Count) _parts.Children.RemoveAt(k); _parts.Children.Insert(k, PartRow()); }
            var row = (Grid)_parts.Children[k];
            ((TextBlock)row.Children[0]).Text = $"Connection {k + 1}";
            ((TextBlock)row.Children[1]).Text = cursor >= end ? $"Done, {Format.Bytes(size)}" : $"{Format.Bytes(cursor - start)} of {Format.Bytes(size)}";
            ((ProgressBar)row.Children[2]).Value = (cursor - start) * 100.0 / size;
        }
    }

    /// <summary>At the end every connection's range is complete; the engine no longer reports them.</summary>
    private void MarkPartsDone()
    {
        if (_segments is null) return;
        foreach (var s in _segments.OfType<JsonObject>()) s["cursor"] = s["end"]!.GetValue<long>();
        DrawMap();
        DrawParts();
    }

    private static Grid PartRow()
    {
        var row = new Grid { RowSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = Text(caption: true);
        var amount = Text(caption: true, secondary: true);
        var bar = new ProgressBar { Maximum = 100 };
        Grid.SetColumn(amount, 1);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        row.Children.Add(name);
        row.Children.Add(amount);
        row.Children.Add(bar);
        return row;
    }

    // ----- actions -----

    private async Task TogglePause()
    {
        var resume = _item.CanResume;
        await _core.CallAsync(new { type = resume ? "resume" : "pause", id = _item.Id });
    }

    private async Task CancelDownload()
    {
        var dialog = new ContentDialog
        {
            Title = "Cancel this download?",
            Content = "It is removed from the list, and the part already downloaded is deleted.",
            PrimaryButtonText = "Cancel download",
            CloseButtonText = "Keep downloading",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _core.CallAsync(new { type = "remove", id = _item.Id, deleteFile = false });
        Close();
    }

    public void ShowOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }
}
