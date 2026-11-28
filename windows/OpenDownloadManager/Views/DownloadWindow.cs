using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
public sealed partial class DownloadWindow : Window
{
    private const int WindowWidth = 520;

    /// <summary>The spacing rhythm: between sections, and inside a section.</summary>
    private const double Section = 16, Inner = 6;

    private readonly CoreClient _core;
    private readonly DownloadItem _item;
    private readonly DispatcherQueueTimer _timer;

    private readonly TextBlock _route = Text(caption: true, secondary: true);
    private readonly TextBlock _headline = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = IconButton("", "Pause");
    private readonly Button _cancel = IconButton("", "Cancel download");
    private readonly Canvas _graph = new();
    private readonly Border _graphFrame;

    // In place of the graph while the file is put together.
    private readonly Border _finishingPanel;
    private readonly TextBlock _finishingText = Text(secondary: true);
    /// <summary>The download in progress; the end view (DownloadWindow.End.cs) replaces it once done or failed.</summary>
    private readonly StackPanel _progressBody;
    private readonly TextBlock _speedLabel = Text(caption: true);
    private readonly Grid _facts = new() { ColumnSpacing = 12, RowSpacing = Inner };
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly Dictionary<string, TextBlock> _labels = [];
    private readonly Button _detailsToggle;
    private readonly FontIcon _detailsChevron = new() { Glyph = "", FontSize = 10 };
    private readonly TextBlock _detailsLabel = new() { Text = "More details" };
    private readonly StackPanel _details = new() { Spacing = Inner + 2, Visibility = Visibility.Collapsed };
    private readonly Grid _map = new() { Height = 6, CornerRadius = new CornerRadius(3) };
    private readonly StackPanel _parts = new() { Spacing = 12 };
    private readonly TextBlock _connSummary = Text(caption: true, secondary: true);
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
        SystemBackdrop = Appearance.Backdrop();

        // ----- header: route, then "54% complete" with pause and cancel -----
        var headline = new Grid { ColumnSpacing = 4, MinHeight = 34 };
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
            Style = CardStyle,
            Child = _graph,
        };
        _graphFrame.SizeChanged += (_, _) => BuildGraph();

        _finishingPanel = new Border
        {
            Height = 92,
            BorderThickness = new Thickness(1),
            Style = CardStyle,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { new ProgressRing { IsActive = true, Width = 22, Height = 22 }, _finishingText },
            },
        };
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
        ToolTipService.SetToolTip(_map, "The whole file: each chunk is one connection's part, filled as it arrives");
        _connSummary.HorizontalAlignment = HorizontalAlignment.Right;
        _details.Children.Add(new Grid
        {
            Children =
            {
                Text(caption: true, secondary: true, text: "Connections"),
                _connSummary,
            },
        });
        _details.Children.Add(_map);
        _details.Children.Add(new Border
        {
            Height = 168,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Style = CardStyle,
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

        // Sections a fixed distance apart, with tighter spacing inside each; the window's height then
        // follows the content (FitHeight), so nothing is left empty at the bottom.
        var body = _progressBody = new StackPanel { Padding = new Thickness(20, 12, 20, 20), Spacing = Section, VerticalAlignment = VerticalAlignment.Top };
        body.Children.Add(new StackPanel { Spacing = 2, Children = { _route, headline } });
        body.Children.Add(new StackPanel { Spacing = Inner, Children = { _speedLabel, new Grid { Children = { _graphFrame, _finishingPanel } } } });
        body.Children.Add(_facts);
        body.Children.Add(footer);
        body.Children.Add(_details);
        var root = new Grid { VerticalAlignment = VerticalAlignment.Top, Children = { body, BuildEnd() } };
        root.KeyDown += OnKey;
        Content = root;
        root.Loaded += (_, _) => FitHeight();

        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, 400));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
        }
        CenterOnScreen();
        ViewSettings.Current.Track(this);
        Appearance.Track(this);

        _item.PropertyChanged += OnItemChanged;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Tick += async (_, _) => await Poll();

        ThemeColors.Changed += OnThemeChanged;
        AfterDownload.ExtractChanged += OnExtractChanged;
        Closed += (_, _) =>
        {
            ThemeColors.Changed -= OnThemeChanged;
            AfterDownload.ExtractChanged -= OnExtractChanged;
            _timer.Stop();
            StopAnimation();
            _item.PropertyChanged -= OnItemChanged;
        };
        // The graph starts with what was recorded before this window opened.
        var history = SpeedHistory.Get(_item.Id);
        if (history.Count > 0 && _item.TotalSize is > 0)
        {
            _samples.AddRange(history);
            _shownProgress = Math.Min(_samples[^1].At, _item.Downloaded / (double)_item.TotalSize.Value);
        }
        Refresh();
    }

    private void OnThemeChanged()
    {
        BuildGraph();
        DrawMap();
        PaintBadge();
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static Style TextStyle(bool caption, bool secondary) => ThemedStyles.Text(caption, secondary);
    private static Style CardStyle => ThemedStyles.Card(secondary: true);
    private static Style TrackStyle => ThemedStyles.Get("Grid", null, ("Background", "ControlStrokeColorDefaultBrush"));
    private static Style MapStyle => ThemedStyles.Get("Border", null, ("Background", "ControlStrokeColorDefaultBrush"));
    private static Style GridLineStyle => ThemedStyles.Get("Line", null, ("Stroke", "DividerStrokeColorDefaultBrush"));
    private static Style SpeedLineStyle => ThemedStyles.Get("Line", null, ("Stroke", "TextFillColorSecondaryBrush"));
    private static Style DividerStyle => ThemedStyles.Get("Border", null, ("BorderBrush", "DividerStrokeColorDefaultBrush"));

    private static TextBlock Text(bool caption = false, bool secondary = false, string text = "") => new()
    {
        Text = text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        Style = TextStyle(caption, secondary),
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
            : i.IsFinishing ? "Finishing…"
            : percent;
        Title = done ? $"Done – {i.DisplayName}"
            : i.IsFinishing ? $"Finishing – {i.DisplayName}"
            : i.IsFailed ? $"Failed – {i.DisplayName}"
            : $"{(i.TotalSize is > 0 ? $"{i.Progress:0}% complete" : "Downloading")} – {i.DisplayName}";

        _values["Name:"].Text = i.DisplayName;
        ToolTipService.SetToolTip(_values["Name:"], i.DisplayName);
        _values["Time remaining:"].Text = done ? (i.SaveDir ?? "") : i.IsFailed ? "—" : i.TimeLeftText is { Length: > 0 } t ? $"About {t}" : "Calculating…";
        _values["Size remaining:"].Text = done ? host
            : i.TotalSize is > 0
            ? $"{Format.Bytes(Math.Max(0, i.TotalSize.Value - i.Downloaded))} ({Format.Bytes(i.TotalSize.Value)})"
            : i.Downloaded > 0 ? $"Unknown ({Format.Bytes(i.Downloaded)} so far)" : "Unknown";
        _labels["Size remaining:"].Text = done ? "From:" : "Size remaining:";
        ToolTipService.SetToolTip(_values["Size remaining:"], done ? i.Url : null);
        // A failed download shows what went wrong where the time would be; a finished one, its folder.
        var failed = i.IsFailed && i.Error is not null;
        _labels["Time remaining:"].Text = failed ? "Problem:" : done ? "Saved to:" : "Time remaining:";
        if (failed) _values["Time remaining:"].Foreground = i.StatusBrush;
        else _values["Time remaining:"].ClearValue(TextBlock.ForegroundProperty);
        if (failed) _values["Time remaining:"].Text = i.Error!;
        ToolTipService.SetToolTip(_values["Time remaining:"], failed ? i.Error : done ? i.FilePath : null);
        // Done, the route line would only repeat the rows below.
        _route.Visibility = done ? Visibility.Collapsed : Visibility.Visible;

        _pause.Visibility = done || i.IsFinishing ? Visibility.Collapsed : Visibility.Visible;

        // The graph gives way to what is happening now: putting the file together, then done.
        _finishingText.Text = i.Kind == "video" ? "Putting the video together…" : "Saving the file…";
        _finishingPanel.Visibility = i.IsFinishing ? Visibility.Visible : Visibility.Collapsed;
        _graphFrame.Visibility = done || i.IsFinishing ? Visibility.Collapsed : Visibility.Visible;
        _speedLabel.Visibility = done || i.IsFinishing ? Visibility.Collapsed : Visibility.Visible;
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
        _detailsToggle.Visibility = i.Kind == "video" || done ? Visibility.Collapsed : Visibility.Visible;
        if (done && _expanded) SetExpanded(false);

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

        // Finished or failed: the end view takes the window's place; back to progress on a retry.
        var ended = done || i.IsFailed;
        var wasEnded = _endBody.Visibility == Visibility.Visible;
        if (ended) ShowEnd(animate: !wasEnded);
        _endBody.Visibility = ended ? Visibility.Visible : Visibility.Collapsed;
        _progressBody.Visibility = ended ? Visibility.Collapsed : Visibility.Visible;
        if (ended != wasEnded) FitHeight();
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
        var accent = ThemeColors.Accent;
        for (var k = 1; k < 4; k++)
            _graph.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * k / 4, Y2 = h * k / 4, Style = GridLineStyle, StrokeThickness = 1 });
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
        _speedLine = new Line { X1 = 0, X2 = w, Style = SpeedLineStyle, StrokeThickness = 1, Opacity = 0, };
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

    /// <summary>Makes the window exactly as tall as its content.</summary>
    private void FitHeight()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        var scale = root.XamlRoot.RasterizationScale;
        var width = AppWindow.ClientSize.Width / scale;
        root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var height = (int)Math.Ceiling(root.DesiredSize.Height * scale);
        if (height > 0 && height != AppWindow.ClientSize.Height)
            AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(AppWindow.ClientSize.Width, height));
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _detailsChevron.Glyph = expanded ? "" : "";
        _detailsLabel.Text = expanded ? "Fewer details" : "More details";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_detailsToggle, _detailsLabel.Text);
        FitHeight();
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

    /// <summary>
    /// The whole file as one bar, rounded only at its ends: each connection's part at true scale,
    /// filled as far as it has arrived.
    /// </summary>
    private void DrawMap()
    {
        _map.Children.Clear();
        _map.ColumnDefinitions.Clear();
        if (_segments is null || _total <= 0) return;
        var col = 0;
        foreach (var s in _segments.OfType<JsonObject>().OrderBy(s => s["start"]!.GetValue<long>()))
        {
            long start = s["start"]!.GetValue<long>(), cursor = s["cursor"]!.GetValue<long>(), end = s["end"]!.GetValue<long>();
            if (end <= start) continue;
            _map.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(end - start, GridUnitType.Star) });
            var chunk = Bar(cursor - start, end - cursor, radius: 0);
            Grid.SetColumn(chunk, col++);
            _map.Children.Add(chunk);
        }
    }

    /// <summary>A rounded track, filled in the accent colour for <paramref name="done"/> of done + left.</summary>
    private static Grid Bar(long done, long left, double radius)
    {
        var bar = new Grid { CornerRadius = new CornerRadius(radius), Style = TrackStyle };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, done), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, left), GridUnitType.Star) });
        bar.Children.Add(new Border { Background = ThemeColors.Brush(() => ThemeColors.Accent, "accent"), CornerRadius = new CornerRadius(radius) });
        return bar;
    }

    private static void SetBar(Grid bar, long done, long left)
    {
        bar.ColumnDefinitions[0].Width = new GridLength(Math.Max(0, done), GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0, left), GridUnitType.Star);
    }

    /// <summary>One compact line per connection: its number, a slim bar, and how much has arrived.</summary>
    private void DrawParts()
    {
        var segs = _segments?.OfType<JsonObject>().OrderBy(s => s["start"]!.GetValue<long>()).ToList() ?? [];
        while (_parts.Children.Count > segs.Count) _parts.Children.RemoveAt(_parts.Children.Count - 1);
        if (segs.Count == 0)
        {
            _connSummary.Text = "";
            if (_parts.Children.Count == 0) _parts.Children.Add(Text(caption: true, secondary: true, text: "Shown while the download runs."));
            return;
        }
        var finished = 0;
        for (var k = 0; k < segs.Count; k++)
        {
            long start = segs[k]["start"]!.GetValue<long>(), cursor = segs[k]["cursor"]!.GetValue<long>(), end = segs[k]["end"]!.GetValue<long>();
            var size = Math.Max(1, end - start);
            var done = cursor >= end;
            if (done) finished++;
            if (k >= _parts.Children.Count || _parts.Children[k] is not Grid { Tag: "part" })
            {
                if (k < _parts.Children.Count) _parts.Children.RemoveAt(k);
                _parts.Children.Insert(k, PartRow());
            }
            var row = (Grid)_parts.Children[k];
            // Its number while it runs, a tick once its part is in.
            ((TextBlock)row.Children[0]).Text = done ? "✓" : $"{k + 1}";
            SetBar((Grid)row.Children[1], cursor - start, end - cursor);
            ((TextBlock)row.Children[2]).Text = done
                ? Format.Bytes(size)
                : $"{Format.Bytes(cursor - start)} / {Format.Bytes(size)}";
            ToolTipService.SetToolTip(row, $"Connection {k + 1}: {(cursor - start) * 100.0 / size:0}%");
        }
        var running = segs.Count - finished;
        _connSummary.Text = running > 0 ? $"{running} active · {finished} done" : $"All {segs.Count} done";
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
        var row = new Grid { Tag = "part", ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        var badge = Text(caption: true, secondary: true);
        badge.VerticalAlignment = VerticalAlignment.Center;
        var bar = Bar(0, 1, radius: 2);
        bar.Height = 4;
        bar.VerticalAlignment = VerticalAlignment.Center;
        var amount = Text(caption: true, secondary: true);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        amount.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(amount, 2);
        row.Children.Add(badge);
        row.Children.Add(bar);
        row.Children.Add(amount);
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
            RequestedTheme = ((FrameworkElement)Content).ActualTheme,
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
