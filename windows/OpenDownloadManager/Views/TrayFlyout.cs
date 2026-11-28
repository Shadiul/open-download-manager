using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using OpenDownloadManager.ViewModels;
using Windows.Graphics;

namespace OpenDownloadManager.Views;

/// <summary>
/// The flyout a left-click on the tray icon opens, above the tray like Windows' own flyouts: the
/// total speed and overall progress, the unfinished downloads (each row filled with its progress, as
/// in the main window) with Pause or Resume, a field to paste a link, Pause all or Resume all, the
/// "when downloads finish" action, and the way to the full window. With nothing to download it shows
/// the files that finished last. It closes when it loses focus or on Esc.
///
/// It only does work while it is open: a half-second tick reads the downloads, and the rows (with
/// their fill animation) are taken down when it closes.
/// </summary>
public sealed class TrayFlyout : Window
{
    private const double FlyoutWidth = 360;
    private const double ListMaxHeight = 5 * 52 + 4 * 4 + 16;

    private readonly MainViewModel _vm;
    private readonly CoreClient _core;
    private readonly Grid _root;
    private readonly StackPanel _head, _empty;
    private readonly TextBlock _speed, _speedUnit, _summary;
    private readonly Grid _total;
    private readonly DownloadItem _totals = new(0);
    private readonly InfoBar _problem;
    private readonly ScrollViewer _scroll;
    private readonly Border _listBand;
    private readonly StackPanel _list, _recent;
    private readonly TextBox _link;
    private readonly TextBlock _linkError;
    private readonly Button _pauseResume;
    private readonly TextBlock _pauseResumeText;
    private readonly FontIcon _pauseResumeIcon;
    private readonly DropDownButton _whenDone;
    private readonly TextBlock _whenDoneText;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<ulong, Row> _rows = [];
    private List<ulong> _order = [];
    private RectInt32 _anchor;
    private long _closedAt;
    private bool _byKeyboard;

    public bool IsOpen { get; private set; }

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public TrayFlyout(MainViewModel vm, CoreClient core)
    {
        _vm = vm;
        _core = core;
        Title = AppPaths.AppName;

        // ----- header: total speed, a summary line, and the overall progress as a dotted strip -----
        _speed = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display"), Style = ThemedStyles.Text() };
        _speedUnit = new TextBlock { FontSize = 14, Margin = new Thickness(4, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom, Style = ThemedStyles.Text(secondary: true) };
        _summary = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Style = ThemedStyles.Text(secondary: true) };
        _total = FillGrid(6, 2);
        _total.Margin = new Thickness(0, 12, 0, 0);
        _total.DataContext = null;
        _total.Loaded += (_, _) => RowFill.Attach(_total);
        _head =new StackPanel
        {
            Padding = new Thickness(16, 14, 16, 14),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _speed, _speedUnit } },
                _summary,
                _total,
            },
        };
        AutomationProperties.SetLiveSetting(_summary, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        // Each stopped row has its own Retry, so the banner only says what happened.
        _problem = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false, Margin = new Thickness(8, 8, 8, 0) };

        // ----- the downloads -----
        _list = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
        AutomationProperties.SetName(_list, "Downloads");
        _scroll = new ScrollViewer { Content = _list, MaxHeight = ListMaxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // ----- nothing downloading: the latest finished files -----
        _recent = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 8, 8) };
        AutomationProperties.SetName(_recent, "Finished recently");
        var seeAll = new HyperlinkButton { Content = "See all", Padding = new Thickness(4, 2, 4, 2), FontSize = 12 };
        seeAll.Click += (_, _) => { Close(); App.Current?.ShowFinished(); };
        var recentHead = new Grid { Margin = new Thickness(16, 8, 12, 2) };
        recentHead.Children.Add(new TextBlock { Text = "Finished recently", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Style = ThemedStyles.Text(secondary: true) });
        seeAll.HorizontalAlignment = HorizontalAlignment.Right;
        recentHead.Children.Add(seeAll);
        _empty = new StackPanel
        {
            Children =
            {
                new StackPanel
                {
                    Padding = new Thickness(16, 24, 16, 8),
                    Spacing = 2,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 32, Margin = new Thickness(0, 0, 0, 8), Style = ThemedStyles.Get("FontIcon", null, ("Foreground", "TextFillColorTertiaryBrush")) },
                        new TextBlock { Text = "Nothing downloading", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Style = ThemedStyles.Text() },
                        new TextBlock { Text = "Paste a link below, or download from your browser.", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Style = ThemedStyles.Text(secondary: true) },
                    },
                },
                recentHead,
                _recent,
            },
        };

        // ----- paste a link -----
        _link = new TextBox { PlaceholderText = "Paste a link to download", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_link, "Link to download");
        _link.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AddLink(); }
        };
        _link.TextChanged += (_, _) => _linkError!.Visibility = Visibility.Collapsed;
        var download = new Button { Content = "Download", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        download.Click += (_, _) => AddLink();
        Grid.SetColumn(download, 1);
        var linkRow = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { _link, download } };
        _linkError = new TextBlock { FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var add = new StackPanel { Padding = new Thickness(16, 12, 16, 12), Children = { linkRow, _linkError } };

        // ----- footer: pause or resume all, when downloads finish, settings, open -----
        _pauseResumeIcon = new FontIcon { FontSize = 14 };
        _pauseResumeText = new TextBlock();
        _pauseResume = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _pauseResumeIcon, _pauseResumeText } } };
        _pauseResume.Click += async (_, _) =>
        {
            await _core.CallAsync(new { type = _pauseResumeText.Text == "Pause all" ? "pauseAll" : "resumeAll" });
            Refresh();
        };
        _whenDoneText = new TextBlock();
        var whenDoneMenu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        whenDoneMenu.Opening += (_, _) => AfterDownload.FillMenu(whenDoneMenu.Items, _core, AfterDownload.QueueAction(_core));
        _whenDone = new DropDownButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 14 }, _whenDoneText } },
            Flyout = whenDoneMenu,
        };
        ToolTipService.SetToolTip(_whenDone, "When downloads finish");
        AfterDownload.QueueActionChanged += value => DispatcherQueue.TryEnqueue(() => ShowQueueAction(value));
        var settings = IconButton("", "Settings", () => { Close(); App.Current?.ShowSettings(); });
        var open = IconButton("", "Open Open Download Manager", () => { Close(); App.Current?.ShowWindow(); });
        Grid.SetColumn(_whenDone, 1);
        Grid.SetColumn(settings, 3);
        Grid.SetColumn(open, 4);
        var foot = new Grid
        {
            Padding = new Thickness(12, 8, 8, 8),
            ColumnSpacing = 4,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { _pauseResume, _whenDone, settings, open },
        };
        var footBand = new Border
        {
            Child = foot,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Style = ThemedStyles.Get("Border", null, ("BorderBrush", "DividerStrokeColorDefaultBrush"), ("Background", "LayerFillColorAltBrush")),
        };

        _root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        var listBand = Divided(new StackPanel { Children = { _problem, _scroll } });
        var addBand = Divided(add);
        _listBand = listBand;
        _root.Children.Add(_head);
        _root.Children.Add(listBand);
        _root.Children.Add(_empty);
        _root.Children.Add(addBand);
        _root.Children.Add(footBand);
        Grid.SetRow(_head, 0);
        Grid.SetRow(listBand, 1);
        Grid.SetRow(_empty, 1);
        Grid.SetRow(addBand, 2);
        Grid.SetRow(footBand, 3);
        _root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close(); }
        };
        AutomationProperties.SetName(_root, AppPaths.AppName);
        Content = _root;

        // A borderless, always-on-top window that is not in Alt+Tab or the taskbar.
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, false);
        }
        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Hwnd, 33, ref corner, sizeof(int));
        ViewSettings.Current.Track(this);
        Appearance.Track(this);

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen) Close();
        };
        _tick.Tick += (_, _) => Refresh();
        ThemeColors.Changed += () => { if (IsOpen) { foreach (var row in _rows.Values) row.Update(); } };
        // Closed by the X of Alt+F4: keep the window, only hide it.
        AppWindow.Closing += (_, e) => { e.Cancel = true; Close(); };
    }

    /// <summary>
    /// For the tray icon's click: opens the flyout, or closes it. A click on the icon while the flyout
    /// is open first takes the focus away (which closes it); that click must not open it again.
    /// </summary>
    public void Toggle(RectInt32 anchor, bool byKeyboard)
    {
        if (IsOpen) { Close(); return; }
        if (Environment.TickCount64 - _closedAt < 400) return;
        Open(anchor, byKeyboard);
    }

    public void Open(RectInt32 anchor, bool byKeyboard)
    {
        _anchor = anchor;
        _byKeyboard = byKeyboard;
        IsOpen = true;
        _link.Text = "";
        _linkError.Visibility = Visibility.Collapsed;
        _total.DataContext = _totals;
        ShowQueueAction(AfterDownload.QueueAction(_core));
        Refresh();
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        visual.Opacity = 0;
        Place(FlyoutWidth, 480);
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
        _tick.Start();
        // Size it to its content once laid out, then slide it in.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsOpen) return;
            Fit();
            Reveal();
            FocusFirst();
        });
    }

    public new void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _closedAt = Environment.TickCount64;
        _tick.Stop();
        AppWindow.Hide();
        // Take the rows down: their fills stop listening and animating while the flyout is hidden.
        _list.Children.Clear();
        _recent.Children.Clear();
        _rows.Clear();
        _order = [];
        _total.DataContext = null;
    }

    // ----- contents -----

    private void Refresh()
    {
        if (!IsOpen) return;
        var all = _vm.All.ToList();
        var pending = all.Where(i => !i.IsCompleted)
            .OrderBy(i => i.IsActive ? 0 : i.IsFailed ? 1 : i.State == "paused" ? 2 : 3)
            .ThenBy(i => i.Position)
            .ToList();
        var nothing = pending.Count == 0;
        _head.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
        _listBand.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;

        var heightBefore = _root.DesiredSize.Height;
        if (nothing) ShowRecent(all);
        else
        {
            if (_recent.Children.Count > 0) _recent.Children.Clear();
            ShowRows(pending);
            ShowTotals(pending);
        }

        var failed = pending.Where(i => i.IsFailed).ToList();
        _problem.IsOpen = failed.Count > 0;
        if (failed.Count > 0)
        {
            _problem.Title = failed.Count == 1 ? "1 download stopped" : $"{failed.Count} downloads stopped";
            _problem.Message = failed.Count == 1
                ? Reason(failed[0])
                : string.Join(" ", failed.Select(Reason).Distinct().Take(2));
        }

        var canPause = all.Any(i => i.CanPause);
        var canResume = all.Any(i => i.State == "paused" && !i.AwaitingConfirmation);
        _pauseResume.Visibility = canPause || canResume ? Visibility.Visible : Visibility.Collapsed;
        _pauseResumeText.Text = canPause ? "Pause all" : "Resume all";
        AutomationProperties.SetName(_pauseResume, _pauseResumeText.Text);
        _pauseResumeIcon.Glyph = canPause ? "" : "";

        _root.Measure(new Windows.Foundation.Size(FlyoutWidth, double.PositiveInfinity));
        if (Math.Abs(_root.DesiredSize.Height - heightBefore) > 0.5) Fit();
    }

    private static string Reason(DownloadItem item) =>
        item.LinkExpired ? $"The link for {item.DisplayName} expired." : $"{item.DisplayName}: {item.Error ?? "failed"}.";

    private void ShowRows(List<DownloadItem> items)
    {
        var order = items.Select(i => i.Id).ToList();
        if (!order.SequenceEqual(_order))
        {
            foreach (var gone in _rows.Keys.Except(order).ToList()) _rows.Remove(gone);
            _list.Children.Clear();
            foreach (var item in items)
            {
                if (!_rows.TryGetValue(item.Id, out var row)) _rows[item.Id] = row = new Row(this, item, finished: false);
                _list.Children.Add(row.Root);
            }
            _order = order;
        }
        foreach (var row in _rows.Values) row.Update();
    }

    private void ShowRecent(List<DownloadItem> all)
    {
        if (_list.Children.Count > 0) { _list.Children.Clear(); _rows.Clear(); _order = []; }
        var latest = all.Where(i => i.IsCompleted).OrderByDescending(i => i.FinishedAt).Take(3).ToList();
        var ids = latest.Select(i => i.Id).ToList();
        if (ids.SequenceEqual(_recent.Children.OfType<FrameworkElement>().Select(e => (ulong)e.Tag))) return;
        _recent.Children.Clear();
        foreach (var item in latest)
        {
            var row = new Row(this, item, finished: true);
            row.Root.Tag = item.Id;
            row.Update();
            _recent.Children.Add(row.Root);
        }
        foreach (var child in ((StackPanel)_recent.Parent).Children.Skip(1)) child.Visibility = latest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTotals(List<DownloadItem> pending)
    {
        var running = pending.Where(i => i.IsActive).ToList();
        var paused = pending.Count(i => i.State == "paused");
        var queued = pending.Count(i => i.State == "queued");
        var failed = pending.Count(i => i.IsFailed);
        var speed = running.Sum(i => i.Speed);

        if (running.Count > 0)
        {
            var text = Format.Speed(speed);
            var space = text.IndexOf(' ');
            _speed.Text = text[..space];
            _speedUnit.Text = text[(space + 1)..];
        }
        else
        {
            _speed.Text = queued > 0 && paused == 0 ? "Queued" : paused > 0 ? "Paused" : "Stopped";
            _speedUnit.Text = "";
        }

        var parts = new List<string>();
        if (running.Count > 0) parts.Add($"{running.Count} downloading");
        if (paused > 0) parts.Add($"{paused} paused");
        if (queued > 0) parts.Add($"{queued} queued");
        if (failed > 0) parts.Add($"{failed} stopped");
        var sized = running.Where(i => i.TotalSize is > 0).ToList();
        if (speed > 0 && sized.Count > 0)
        {
            var left = Format.Duration(sized.Sum(i => i.TotalSize!.Value - i.Downloaded) / (double)speed);
            if (left.Length > 0) parts.Add($"about {left} left");
        }
        _summary.Text = string.Join(" · ", parts);

        // The overall progress: everything not finished, as one download.
        var known = pending.Where(i => i.TotalSize is > 0 && !i.IsFailed).ToList();
        var job = new JsonObject
        {
            ["id"] = 0,
            ["state"] = running.Count > 0 ? "downloading" : paused > 0 ? "paused" : "queued",
            ["totalSize"] = known.Count > 0 ? known.Sum(i => i.TotalSize!.Value) : null,
            ["downloaded"] = known.Sum(i => i.Downloaded),
            ["speed"] = speed,
        };
        _totals.Update(job);
        _total.Opacity = known.Count > 0 && running.Count + paused > 0 ? 1 : 0;
        var share = known.Count > 0 ? known.Sum(i => i.Downloaded) * 100.0 / known.Sum(i => i.TotalSize!.Value) : 0;
        AutomationProperties.SetName(_total, $"{share:0}% of all downloads");
    }

    private void ShowQueueAction(string value)
    {
        _whenDoneText.Text = value == "nothing" ? "When done" : AfterDownload.Label(value);
        AutomationProperties.SetName(_whenDone, $"When downloads finish: {AfterDownload.Label(value)}");
    }

    private async void AddLink()
    {
        var text = _link.Text.Trim();
        if (text.Length == 0)
        {
            Close();
            App.Current?.ShowAddDialog(null);
            return;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
        {
            ShowLinkError("Paste a full link that starts with http:// or https://.");
            return;
        }
        var reply = await _core.CallAsync(new { type = "add", url = text });
        if (reply["ok"]?.GetValue<bool>() == false)
        {
            ShowLinkError(reply["error"]?.GetValue<string>() ?? "Could not add that link.");
            return;
        }
        _link.Text = "";
        Refresh();
    }

    private void ShowLinkError(string message)
    {
        _linkError.Text = message;
        _linkError.Foreground = ThemeColors.Brush(() => ThemeColors.Critical, "critical");
        _linkError.Visibility = Visibility.Visible;
        Fit();
    }

    // ----- placement and motion -----

    /// <summary>Sizes the window to its content, keeping the edge next to the taskbar where it is.</summary>
    private void Fit()
    {
        if (!IsOpen) return;
        _root.Measure(new Windows.Foundation.Size(FlyoutWidth, double.PositiveInfinity));
        var height = Math.Max(120, _root.DesiredSize.Height);
        Place(FlyoutWidth, height);
    }

    /// <summary>
    /// Above the tray icon (or beside or below it when the taskbar is on another edge), centred on
    /// the icon and kept 12 pixels inside the screen's free area.
    /// </summary>
    private void Place(double width, double height)
    {
        var display = DisplayArea.GetFromRect(_anchor, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var bounds = display.OuterBounds;
        var scale = DpiFor(display) / 96.0;
        int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale), gap = (int)Math.Round(12 * scale);
        h = Math.Min(h, work.Height - 2 * gap);
        var cx = _anchor.X + _anchor.Width / 2;
        var cy = _anchor.Y + _anchor.Height / 2;
        int x = Math.Clamp(cx - w / 2, work.X + gap, work.X + work.Width - w - gap);
        int y = work.Y + work.Height - h - gap;
        if (work.Y > bounds.Y) y = work.Y + gap; // taskbar at the top
        else if (work.X > bounds.X) { x = work.X + gap; y = Math.Clamp(cy - h / 2, work.Y + gap, work.Y + work.Height - h - gap); }
        else if (work.Width < bounds.Width) { x = work.X + work.Width - w - gap; y = Math.Clamp(cy - h / 2, work.Y + gap, work.Y + work.Height - h - gap); }
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private static uint DpiFor(DisplayArea display)
    {
        var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 96;
    }

    private void Reveal()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        var animation = ViewSettings.Current.Animation;
        if (animation == "off" || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.Opacity = 1;
            return;
        }
        var compositor = visual.Compositor;
        var duration = TimeSpan.FromMilliseconds(animation == "lively" ? 280 : 180);
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        var rise = compositor.CreateVector3KeyFrameAnimation();
        ElementCompositionPreview.SetIsTranslationEnabled(_root, true);
        rise.InsertKeyFrame(0, new Vector3(0, animation == "lively" ? 16 : 8, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = duration;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", rise);
    }

    private void FocusFirst()
    {
        // Opened with the mouse: the link field, so Ctrl+V and Enter add a download straight away.
        // Opened with the keyboard: the first control, with its focus rectangle.
        if (_byKeyboard && FocusManager.FindFirstFocusableElement(_root) is Control first) first.Focus(FocusState.Keyboard);
        else _link.Focus(FocusState.Programmatic);
    }

    // ----- building blocks -----

    /// <summary>The structure RowFill draws into: a track and the lit part with its two sweep layers.</summary>
    private static Grid FillGrid(double height, double radius) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(radius),
        IsHitTestVisible = false,
        Children = { new Border(), new Grid { Children = { new Border { Visibility = Visibility.Collapsed }, new Border { Visibility = Visibility.Collapsed } } } },
    };

    private static Border Divided(UIElement child) => new()
    {
        Child = child,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Style = ThemedStyles.Get("Border", null, ("BorderBrush", "DividerStrokeColorDefaultBrush")),
    };

    private static Button IconButton(string glyph, string name, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>One download: filled with its progress like a main-window row, with its actions.</summary>
    private sealed class Row
    {
        private readonly TrayFlyout _owner;
        private readonly DownloadItem _item;
        private readonly bool _finished;
        private readonly Grid _fill;
        private readonly Image _image = new() { Width = 24, Height = 24 };
        private readonly FontIcon _glyph = new() { FontSize = 20 };
        private readonly TextBlock _name, _meta;
        private readonly Button _folder;
        public Grid Root { get; }
        public Button? Primary { get; }

        public Row(TrayFlyout owner, DownloadItem item, bool finished)
        {
            _owner = owner;
            _item = item;
            _finished = finished;
            _fill = FillGrid(double.NaN, 6);
            _fill.DataContext = finished ? null : item;
            if (!finished) _fill.Loaded += (_, _) => RowFill.Attach(_fill);

            _name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Style = ThemedStyles.Text() };
            _meta = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Style = ThemedStyles.Text(secondary: true) };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _meta } };
            var icon = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, Children = { _glyph, _image } };

            _folder = IconButton("", $"Show {item.DisplayName} in folder", () => Shell.ShowInFolder(item.FilePath, item.SaveDir));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (finished)
            {
                actions.Children.Add(IconButton("", $"Open {item.DisplayName}", () => { if (item.FilePath is { } path) { owner.Close(); Shell.Open(path); } }));
                actions.Children.Add(_folder);
            }
            else
            {
                // Show in folder appears on hover or focus; pause, resume or retry is always there.
                _folder.Opacity = 0;
                Primary = IconButton("", "", Act);
                actions.Children.Add(_folder);
                actions.Children.Add(Primary);
            }

            Grid.SetColumn(text, 1);
            Grid.SetColumn(actions, 2);
            var content = new Grid
            {
                Padding = new Thickness(8, 6, 4, 6),
                ColumnSpacing = 12,
                ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                Children = { icon, text, actions },
            };
            Root = new Grid { MinHeight = 52, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _fill, content } };
            var hover = ThemedStyles.Get("Grid", null, ("Background", "SubtleFillColorSecondaryBrush"));
            Root.PointerEntered += (_, _) => { Root.Style = hover; Root.ClearValue(Panel.BackgroundProperty); _folder.Opacity = 1; };
            Root.PointerExited += (_, _) => { Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); if (!finished) _folder.Opacity = FocusWithin() ? 1 : 0; };
            _folder.GotFocus += (_, _) => _folder.Opacity = 1;
            _folder.LostFocus += (_, _) => { if (!finished) _folder.Opacity = 0; };
            // A click on the row itself opens the file when finished, or the download's progress window.
            Root.Tapped += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
                owner.Close();
                if (finished) { if (item.FilePath is { } path) Shell.Open(path); }
                else App.Current?.ShowProgressWindow(item);
            };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadItem.Icon)) ShowIcon();
            };
            ShowIcon();
        }

        private bool FocusWithin() => FocusManager.GetFocusedElement(Root.XamlRoot) is DependencyObject f && IsWithin(f, Root);

        private static bool IsWithin(DependencyObject d, DependencyObject root)
        {
            for (var e = d; e is not null; e = VisualTreeHelper.GetParent(e)) if (e == root) return true;
            return false;
        }

        private static bool IsInButton(DependencyObject d)
        {
            for (var e = d; e is not null; e = VisualTreeHelper.GetParent(e)) if (e is Button) return true;
            return false;
        }

        private void ShowIcon()
        {
            _image.Source = _item.Icon;
            _image.Visibility = _item.Icon is null ? Visibility.Collapsed : Visibility.Visible;
            _glyph.Visibility = _item.Icon is null ? Visibility.Visible : Visibility.Collapsed;
            _glyph.Glyph = _item.Glyph;
            _glyph.Foreground = ThemeColors.Brush(() => ThemeColors.ForCategory(_item.Category), "cat" + _item.Category);
        }

        public void Update()
        {
            var item = _item;
            _name.Text = item.DisplayName;
            _fill.Opacity = item.FillOpacity;
            ShowIcon();
            _meta.Inlines.Clear();
            string? lead = null;
            Brush? leadBrush = null;
            string rest;
            if (_finished)
            {
                rest = Join(item.SizeText, Finished(item.FinishedAt));
            }
            else
            {
                switch (item.State)
                {
                    case "downloading":
                        lead = item.TotalSize is > 0 ? $"{item.Progress:0}%" : Format.Bytes(item.Downloaded);
                        leadBrush = ThemeColors.Brush(() => ThemeColors.AccentText, "accentText");
                        rest = Join(item.SpeedText, item.TimeLeftText.Length > 0 ? $"{item.TimeLeftText} left" : "");
                        break;
                    case "paused" when item.AwaitingConfirmation:
                        lead = "Waiting for you";
                        leadBrush = ThemeColors.Brush(() => ThemeColors.Caution, "caution");
                        rest = item.SizeText;
                        break;
                    case "paused":
                        lead = item.BarText;
                        leadBrush = ThemeColors.Brush(() => ThemeColors.Caution, "caution");
                        rest = item.SizeText;
                        break;
                    case "queued":
                        rest = Join("Queued", item.SizeText);
                        break;
                    case "failed":
                        lead = item.LinkExpired ? "Link expired" : "Failed";
                        leadBrush = ThemeColors.Brush(() => ThemeColors.Critical, "critical");
                        rest = item.LinkExpired ? item.SizeText : item.Error ?? item.SizeText;
                        break;
                    default:
                        rest = item.Detail;
                        break;
                }
            }
            if (lead is not null)
            {
                _meta.Inlines.Add(new Run { Text = lead, Foreground = leadBrush });
                if (rest.Length > 0) _meta.Inlines.Add(new Run { Text = " · " + rest });
            }
            else _meta.Inlines.Add(new Run { Text = rest });

            if (Primary is not null)
            {
                var (glyph, verb) = item.State switch
                {
                    "failed" when item.LinkExpired => ("", "Fix the link in Open Download Manager"),
                    "failed" => ("", "Retry"),
                    "paused" when item.AwaitingConfirmation => ("", "Answer in Open Download Manager"),
                    _ when item.CanPause => ("", "Pause"),
                    _ => ("", "Resume"),
                };
                ((FontIcon)Primary.Content).Glyph = glyph;
                var name = item.LinkExpired || item.AwaitingConfirmation ? verb : $"{verb} {item.DisplayName}";
                AutomationProperties.SetName(Primary, name);
                ToolTipService.SetToolTip(Primary, verb);
                Primary.Visibility = item.IsFinishing ? Visibility.Collapsed : Visibility.Visible;
            }
            AutomationProperties.SetName(_folder, $"Show {item.DisplayName} in folder");
            var plain = string.Concat(_meta.Inlines.OfType<Run>().Select(r => r.Text));
            AutomationProperties.SetName(Root, $"{item.DisplayName}, {plain}");
        }

        private async void Act()
        {
            var item = _item;
            if (item.LinkExpired || item.AwaitingConfirmation)
            {
                _owner.Close();
                if (item.AwaitingConfirmation) App.Current?.ShowNewDownload(item);
                else App.Current?.ShowWindow();
                return;
            }
            var reply = await _owner._core.CallAsync(new { type = item.CanPause ? "pause" : "resume", id = item.Id });
            if (reply["ok"]?.GetValue<bool>() == false) _owner.ShowLinkError(reply["error"]?.GetValue<string>() ?? "The engine refused that.");
            _owner.Refresh();
        }

        private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

        private static string Finished(long at)
        {
            if (at <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
            var ago = DateTime.Now - t;
            if (ago.TotalMinutes < 1) return "just now";
            if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
            if (t.Date == DateTime.Today) return $"today, {t:t}";
            if (t.Date == DateTime.Today.AddDays(-1)) return $"yesterday, {t:t}";
            return t.ToString("d MMM");
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
