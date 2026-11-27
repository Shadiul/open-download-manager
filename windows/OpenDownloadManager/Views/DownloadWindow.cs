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

    // The graph animates at about 30 frames a second, easing toward the engine's latest numbers
    // (which arrive four times a second) and growing one pixel at a time.
    private bool _animating;
    private TimeSpan _lastFrame;
    private double _shownProgress, _shownSpeed, _targetProgress, _targetSpeed, _top;
    private int _frame;
    private Rectangle? _doneArea;
    private PathFigure? _areaFigure;
    private LineSegment? _areaClose;
    private Polyline? _line;
    private Line? _speedLine;
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
        _graphFrame.SizeChanged += (_, _) => BuildGraph();
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
        CenterOnScreen();
        ViewSettings.Current.Track(this);

        _item.PropertyChanged += OnItemChanged;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Tick += async (_, _) => await Poll();

        Closed += (_, _) =>
        {
            _timer.Stop();
            StopAnimation();
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

        // The graph eases toward these on its own timer.
        if (done) _targetProgress = 1;
        else if (i.TotalSize is > 0) _targetProgress = i.Downloaded / (double)i.TotalSize.Value;
        _targetSpeed = i.State == "downloading" ? i.Speed : 0;
        if (_targetProgress + 0.01 < _shownProgress)
        {
            // The download started over: so does the graph.
            _samples.Clear();
            _shownProgress = 0;
            BuildGraph();
        }
        if (i.State == "downloading" || Math.Abs(_targetProgress - _shownProgress) > 0.0005) StartAnimation();
        if (done)
        {
            _timer?.Stop();
            MarkPartsDone();
        }
    }

    // ----- the speed graph -----

    /// <summary>
    /// Creates the graph's shapes: faint grid lines, the shaded part already downloaded, the speed
    /// as a filled line across the progress, and a line at the current speed. Animate() then
    /// changes them in place.
    /// </summary>
    private void BuildGraph()
    {
        _graph.Children.Clear();
        double w = _graphFrame.ActualWidth - 2, h = _graphFrame.ActualHeight - 2;
        if (w <= 0 || h <= 0) return;
        var accent = Res<SolidColorBrush>("AccentFillColorDefaultBrush").Color;
        for (var k = 1; k < 4; k++)
            _graph.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * k / 4, Y2 = h * k / 4, Stroke = Res<Brush>("DividerStrokeColorDefaultBrush"), StrokeThickness = 1 });
        _doneArea = new Rectangle { Height = h, Width = w * _shownProgress, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(28, accent.R, accent.G, accent.B)) };
        _areaFigure = new PathFigure { IsClosed = true };
        _graph.Children.Add(_doneArea);
        _graph.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { _areaFigure } },
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B)),
        });
        _line = new Polyline { Stroke = new SolidColorBrush(accent), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        _graph.Children.Add(_line);
        _speedLine = new Line { X1 = 0, X2 = w, Stroke = Res<Brush>("TextFillColorSecondaryBrush"), StrokeThickness = 1, Opacity = 0, };
        _graph.Children.Add(_speedLine);
        Replot();
        Animate(0);
    }

    // The graph draws on the display's frame clock (60 frames a second on most screens).
    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        if (dt <= 0) return; // the same frame reported twice
        _lastFrame = now;
        Animate(dt);
    }

    /// <summary>Plots every sample again, after the size or the speed scale changed.</summary>
    private void Replot()
    {
        if (_areaFigure is null || _line is null) return;
        double w = _graphFrame.ActualWidth - 2, h = _graphFrame.ActualHeight - 2;
        _areaFigure.Segments.Clear();
        _line.Points.Clear();
        _areaClose = null;
        if (_samples.Count == 0) return;
        _top = Math.Max(_top, _samples.Max(x => x.Speed) * 1.3);
        _areaFigure.StartPoint = new Point(_samples[0].At * w, h);
        foreach (var sample in _samples)
        {
            var pt = PointOf(sample, w, h);
            _areaFigure.Segments.Add(new LineSegment { Point = pt });
            _line.Points.Add(pt);
        }
        _areaClose = new LineSegment { Point = new Point(_samples[^1].At * w, h) };
        _areaFigure.Segments.Add(_areaClose);
    }

    private Point PointOf((double At, double Speed) sample, double w, double h) =>
        new(sample.At * w, h - (_top > 0 ? sample.Speed / _top * h : 0));

    /// <summary>One frame: ease toward the latest numbers and grow the line by whole pixels.</summary>
    private void Animate(double dt)
    {
        double w = _graphFrame.ActualWidth - 2, h = _graphFrame.ActualHeight - 2;
        if (w <= 0 || h <= 0 || _doneArea is null || _areaFigure is null || _line is null || _speedLine is null) return;
        _frame++;

        // Without a known size, the graph creeps forward while bytes arrive.
        if (_item.TotalSize is not > 0 && _targetSpeed > 0) _targetProgress = Math.Min(0.98, _targetProgress + 0.018 * dt);
        // Exponential easing by elapsed time: the same feel at any frame rate.
        var ease = dt <= 0 ? 1 : 1 - Math.Exp(-dt / 0.14);
        _shownProgress += (_targetProgress - _shownProgress) * ease;
        _shownSpeed += (_targetSpeed - _shownSpeed) * ease;
        if (Math.Abs(_targetProgress - _shownProgress) < 0.0002) _shownProgress = _targetProgress;

        if (_targetSpeed > 0 && (_samples.Count == 0 || (_shownProgress - _samples[^1].At) * w >= 1))
        {
            var sample = (_shownProgress, _shownSpeed);
            _samples.Add(sample);
            if (_shownSpeed * 1.1 > _top || _samples.Count == 1)
            {
                // A new peak: widen the scale with headroom and replot.
                _top = _shownSpeed * 1.4;
                Replot();
            }
            else
            {
                var pt = PointOf(sample, w, h);
                if (_areaFigure.Segments.Count == 0) _areaFigure.StartPoint = new Point(pt.X, h);
                if (_areaClose is not null) _areaFigure.Segments.Remove(_areaClose);
                _areaFigure.Segments.Add(new LineSegment { Point = pt });
                _areaClose = new LineSegment { Point = new Point(pt.X, h) };
                _areaFigure.Segments.Add(_areaClose);
                _line.Points.Add(pt);
            }
        }

        _doneArea.Width = Math.Max(0, w * _shownProgress);
        if (_shownSpeed > 1 && _top > 0)
        {
            var y = h - Math.Min(1, _shownSpeed / _top) * h;
            _speedLine.Y1 = _speedLine.Y2 = y;
            _speedLine.Opacity = 0.6;
        }
        else
        {
            _speedLine.Opacity = 0;
        }

        // The number changes a few times a second, so it stays readable.
        if (_frame % 12 == 0 || _targetSpeed == 0)
            _speedLabel.Text = _targetSpeed > 0 ? $"Speed: {Format.Speed((long)_shownSpeed)}" : " ";

        var settled = _targetSpeed == 0 && Math.Abs(_targetProgress - _shownProgress) < 0.0002 && _shownSpeed < 1;
        if (settled) StopAnimation();
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

    private static int _openCount;

    /// <summary>Centred on screen; several at once step down and right so each stays visible.</summary>
    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        var step = 32 * (_openCount++ % 6);
        Closed += (_, _) => _openCount = Math.Max(0, _openCount - 1);
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2 + step, area.Y + (area.Height - size.Height) / 2 + step));
    }

    /// <summary>
    /// Brings the window to the front with focus. A double-click in the list finishes after this
    /// runs and would pull the main window back on top, so it is done again once input settles.
    /// </summary>
    public void ShowOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Once the double-click's mouse input has finished, so the list cannot take focus back.
        // A window that is still being created needs a second, later nudge.
        foreach (var delay in new[] { 150, 450 })
        {
            var later = DispatcherQueue.CreateTimer();
            later.Interval = TimeSpan.FromMilliseconds(delay);
            later.IsRepeating = false;
            later.Tick += (_, _) =>
            {
                AppWindow.MoveInZOrderAtTop();
                SetForegroundWindow(hwnd);
                Activate();
            };
            later.Start();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
