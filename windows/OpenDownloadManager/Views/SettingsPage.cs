using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>
/// The settings, as a page of Fluent settings cards in sections, with a bar at the top that jumps
/// between them. Every change is saved as it is made: engine settings go to the core, app
/// settings to app-settings.json. Appearance changes show at once in the preview at the top of
/// that section and in the list behind.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly CoreClient _core;
    private readonly AppSettings _app;
    private readonly IntPtr _hwnd;
    private JsonObject _settings = [];
    private bool _loading = true;
    private readonly DispatcherTimerLike _saveSoon;

    // Downloads
    private readonly TextBox _folder = new() { MinWidth = 260 };
    private readonly ToggleSwitch _categories = Switch();
    private readonly NumberBox _connections = Number(1, 32);
    private readonly NumberBox _parallel = Number(1, 20);
    private readonly NumberBox _speed = Number(0, 10_000_000);
    private readonly ComboBox _speedUnit = new() { Items = { "MB/s", "KB/s" }, SelectedIndex = 0, MinWidth = 100 };
    private readonly ToggleSwitch _schedule = Switch();
    private readonly TimePicker _start = new() { ClockIdentifier = "24HourClock", MinWidth = 0 };
    private readonly TimePicker _stop = new() { ClockIdentifier = "24HourClock", MinWidth = 0 };

    // After download
    private readonly ToggleSwitch _notifications = Switch();
    private readonly ToggleSwitch _afterOpen = Switch();
    private readonly ToggleSwitch _afterShow = Switch();
    private readonly ToggleSwitch _afterExtract = Switch();
    private readonly CheckBox _afterExtractDelete = new() { Content = "Then move the archive to the Recycle Bin" };
    private readonly ComboBox _whenDone = new() { MinWidth = 220 };
    private readonly ComboBox _countdown = new() { Items = { "10 seconds", "30 seconds", "1 minute", "2 minutes" }, MinWidth = 160 };
    private readonly ToggleSwitch _once = Switch();

    // Browsers and video
    private readonly ToggleSwitch _confirm = Switch();
    private readonly ToggleSwitch _progressWindows = Switch();
    private readonly ToggleSwitch _updateTools = Switch();
    private readonly TextBlock _hostStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _toolsStatus = new() { TextWrapping = TextWrapping.Wrap };

    // Appearance
    private readonly ComboBox _theme = new() { Items = { "Use system setting", "Light", "Dark" }, MinWidth = 180 };
    private readonly ComboBox _backdrop = new() { Items = { "Mica", "Mica Alt", "Acrylic", "Solid" }, MinWidth = 180 };
    private readonly ComboBox _animation = new() { Items = { "Off", "Calm", "Lively" }, MinWidth = 180 };
    private readonly ComboBox _strength = new() { Items = { "Subtle", "Normal", "Vivid" }, MinWidth = 180 };
    private readonly ComboBox _colorBy = new() { Items = { "Download status", "File type" }, MinWidth = 180 };
    private readonly ComboBox _density = new() { Items = { "Compact", "Standard", "Comfortable" }, MinWidth = 180 };
    private readonly GridView _swatches = Picker("Accent colour", followFocus: false);
    private readonly GridView _rowShape = Picker("Across the row", followFocus: true);
    private readonly GridView _rowLook = Picker("Row look", followFocus: true);
    private readonly GridView _columnShape = Picker("In the Progress column", followFocus: true);
    private readonly GridView _columnLook = Picker("Column look", followFocus: true);

    // System
    private readonly ToggleSwitch _startWithWindows = Switch();
    private readonly ToggleSwitch _closeToTray = Switch();
    private readonly ToggleSwitch _clipboard = Switch();

    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly SelectorBar _sections = new();
    private readonly List<(SelectorBarItem Item, FrameworkElement Anchor)> _anchors = [];
    private bool _jumping;

    // Progress is shown in two places, each with its own shape and look: across the row (RowFill)
    // and in the Progress column (ProgressCell).
    private static readonly (string Value, string Label, string Hint)[] RowShapes =
    [
        ("bar", "Bar", "The whole row fills as the download goes."),
        ("underline", "Underline", "A thin line along the bottom of the row."),
        ("none", "Off", "Nothing behind the row."),
    ];

    private static readonly (string Value, string Label, string Hint)[] ColumnShapes =
    [
        ("ring", "Ring", "A small circle that fills as it goes, the percentage beside it."),
        ("bar", "Bar", "A thin bar under the percentage."),
        ("text", "Text only", "The percentage or the state, in words."),
    ];

    private static readonly (string Value, string Label, string Hint)[] Looks =
    [
        ("dots", "Dot matrix", "Lit dots, like an LED panel."),
        ("solid", "Solid", "One flat colour."),
        ("glass", "Glass", "Colour that fades from top to bottom."),
        ("stripes", "Stripes", "Diagonal stripes, or a ring in segments."),
    ];

    public SettingsPage(CoreClient core, AppSettings app, IntPtr hwnd)
    {
        _core = core;
        _app = app;
        _hwnd = hwnd;
        _saveSoon = new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromMilliseconds(500), SaveEngine);
        foreach (var a in AfterDownload.QueueActions) _whenDone.Items.Add(a.Label);

        var page = new StackPanel { Spacing = 4, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(_error);

        // ----- Downloads -----
        var browse = new Button { Content = "Browse…" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_folder, "Download folder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_speed, "Speed limit");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_speedUnit, "Speed limit unit");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_start, "Start time");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_stop, "Stop time");
        browse.Click += async (_, _) => await PickFolder();
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center }, _stop } };
        Section(page, "Downloads", "\uE896");
        page.Children.Add(Card("\uE8B7", "Download folder", "Where finished files go.", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _folder, browse } }));
        page.Children.Add(Card("\uE8F1", "Sort into category folders", "Video, Music, Documents, Programs, Archives and Other, inside the download folder.", _categories));
        page.Children.Add(Card("\uE968", "Connections per download", "More connections are faster on most servers. 1 to 32.", _connections));
        page.Children.Add(Card("\uE8CB", "Downloads at the same time", "The rest wait in the queue.", _parallel));
        page.Children.Add(Card("\uEC4A", "Speed limit", "For all downloads together. 0 means no limit.",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _speed, _speedUnit } }));
        page.Children.Add(Card("\uE787", "Only download between set times", "Downloads outside the window wait in the queue.", _schedule));
        page.Children.Add(Card("\uE823", "Download window", "Can run past midnight, such as 23:00 to 07:00.", times));

        // ----- After download -----
        Section(page, "After download", "\uE73E");
        page.Children.Add(Card("\uEA8F", "Notify me", "When a download finishes or fails.", _notifications));
        page.Children.Add(Card("\uE8E5", "Open the file", "Programs and scripts are never started by themselves.", _afterOpen));
        page.Children.Add(Card("\uE838", "Show it in its folder", "File Explorer opens with the file selected.", _afterShow));
        page.Children.Add(Card("\uF012", "Extract archives", "ZIP, 7z, RAR and TAR files unpack into a folder next to them.", _afterExtract,
            extra: _afterExtractDelete));
        Subhead(page, "When all downloads finish");
        page.Children.Add(Card("\uE7E8", "Then", "Also in the status bar and the tray menu, for just this time.", _whenDone));
        page.Children.Add(Card("\uE916", "Time to cancel", "A countdown shows first, with Cancel and Now.", _countdown));
        page.Children.Add(Card("\uE8ED", "Only once", "Go back to Do nothing afterwards, so a shutdown is never left set by mistake.", _once));

        // ----- Browsers -----
        var reRegister = new Button { Content = "Connect again" };
        reRegister.Click += (_, _) =>
        {
            try { NativeHostRegistration.Register(); } catch (Exception e) { ShowError(e.Message); }
            ShowHostStatus();
        };
        var openExtension = new Button { Content = "Open the extension's folder" };
        openExtension.Click += (_, _) =>
        {
            BundledExtension.Sync();
            OpenFolder(Path.Combine(AppPaths.DataDir, "extension"));
        };
        Section(page, "Browsers", "\uE774");
        page.Children.Add(Card("\uE774", "Browser extension", "Sends your browser's downloads and videos here. It comes with the app; add it to each browser you use.",
            new BrowserList(), wide: true));
        page.Children.Add(Card("\uE71B", "Connection", null, new StackPanel
        {
            Spacing = 8,
            Children = { _hostStatus, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { reRegister, openExtension } } },
        }, wide: true));
        page.Children.Add(Card("\uE8A7", "Ask before each browser download", "Shows the file name, size and folder; the download starts meanwhile.", _confirm));
        page.Children.Add(Card("\uE9D9", "Open a progress window for each download", "Like IDM. You can close it; the download carries on.", _progressWindows));

        // ----- Appearance -----
        Section(page, "Appearance", "\uE790");
        page.Children.Add(Preview());
        page.Children.Add(Card("\uE793", "Theme", null, _theme));
        page.Children.Add(Card("\uE790", "Accent colour", "For buttons, switches, selection and the rows.", null, extra: _swatches));
        page.Children.Add(Card("\uE7F4", "Window material", "Mica takes on the desktop's colours; Acrylic shows it through, blurred.", _backdrop));
        page.Children.Add(Card("\uE9D9", "Progress across the row", "Each download's row fills as it goes.", null, extra: PickerPair(_rowShape, _rowLook)));
        page.Children.Add(Card("\uF16A", "Progress in the Progress column", "A ring or a bar beside the percentage.", null, extra: PickerPair(_columnShape, _columnLook)));
        page.Children.Add(Card("\uE945", "Animation", "A band of light that moves along downloading rows.", _animation));
        page.Children.Add(Card("\uE706", "Fill strength", "How strongly rows are tinted.", _strength));
        page.Children.Add(Card("\uE8EC", "Colour rows by", "File type gives video, music, documents, programs and archives each a colour.", _colorBy));
        page.Children.Add(Card("\uE8FD", "Density", "How much space each download takes in the list.", _density));

        // ----- Video -----
        var openTools = new Button { Content = "Open folder" };
        openTools.Click += (_, _) => OpenFolder(AppPaths.ToolsDir);
        Section(page, "Video", "\uE714");
        page.Children.Add(Card("\uE895", "Update yt-dlp every day", "Video sites change often; a current yt-dlp keeps working.", _updateTools));
        page.Children.Add(Card("\uE90F", "Video tools", null, new StackPanel { Spacing = 8, Children = { _toolsStatus, openTools } }, wide: true));

        // ----- System -----
        Section(page, "System", "\uE770");
        page.Children.Add(Card("\uE768", "Start with Windows", "Starts in the notification area, ready for browser downloads.", _startWithWindows));
        page.Children.Add(Card("\uE921", "Closing the window keeps downloads running", "The app stays in the notification area.", _closeToTray));
        page.Children.Add(Card("\uE77F", "Offer to download links I copy", "For files, and for video pages such as YouTube in Chrome.", _clipboard));

        // ----- About -----
        var openData = new Button { Content = "Open folder" };
        openData.Click += (_, _) => OpenFolder(AppPaths.DataDir);
        var welcome = new Button { Content = "Show the welcome screen" };
        welcome.Click += (_, _) => App.Current?.ShowWelcome();
        Section(page, "About", "\uE946");
        page.Children.Add(Card("\uE946", $"Open Download Manager {typeof(App).Assembly.GetName().Version?.ToString(3)}", "Free and open source under GPL-3.0.", welcome));
        page.Children.Add(Card("\uE8B7", "Data folder", "Download history, settings and app.log.", openData));

        // A click on a card's text or on empty space must not move the focus. Left alone, the press
        // reaches the page's ScrollViewer, which hands focus to its first focusable child (the
        // download folder box at the very top) and scrolls that into view: the page jumped back to
        // the top. Controls handle their own presses before they get here, so they still take focus.
        var content = new Grid { Padding = new Thickness(24, 0, 24, 32), Background = new SolidColorBrush(Colors.Transparent), Children = { page } };
        content.PointerPressed += (_, e) => e.Handled = true;
        _scroller.Content = content;
        _scroller.ViewChanged += (_, _) => FollowScroll();
        _sections.SelectionChanged += (_, _) => JumpToSection();

        var header = new StackPanel
        {
            Padding = new Thickness(24, 4, 24, 4),
            MaxWidth = 1048,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new TextBlock { Text = "Settings", Style = Res<Style>("TitleTextBlockStyle"), Margin = new Thickness(0, 4, 0, 4) },
                _sections,
            },
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(_scroller, 1);
        root.Children.Add(_scroller);
        Content = root;

        Load();
        BuildSwatches();
        BuildProgressPickers();
        SyncSwatches();
        SyncProgressPickers();
        Wire();
        _loading = false;

        // Accent or style changed elsewhere (or Windows' accent): move the selection to match.
        Listen(true);
        Loaded += (_, _) => { Listen(true); SyncSwatches(); SyncProgressPickers(); };
        Unloaded += (_, _) => Listen(false);

        AfterDownload.QueueActionChanged += OnQueueActionChanged;
        Unloaded += (_, _) => AfterDownload.QueueActionChanged -= OnQueueActionChanged;
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static ToggleSwitch Switch() => new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };

    private static NumberBox Number(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        MinWidth = 140,
    };

    // ----- layout -----

    private void Section(Panel page, string title, string glyph)
    {
        var heading = new TextBlock
        {
            Text = title,
            Style = Res<Style>("SubtitleTextBlockStyle"),
            Margin = new Thickness(1, _anchors.Count == 0 ? 12 : 28, 0, 8),
        };
        page.Children.Add(heading);
        var item = new SelectorBarItem { Text = title, Icon = new FontIcon { Glyph = glyph } };
        _sections.Items.Add(item);
        _anchors.Add((item, heading));
    }

    private static void Subhead(Panel page, string title) => page.Children.Add(new TextBlock
    {
        Text = title,
        Style = Res<Style>("BodyStrongTextBlockStyle"),
        Margin = new Thickness(1, 14, 0, 6),
    });

    /// <summary>
    /// A Fluent settings card: icon, title and description on the left, the control on the right.
    /// <paramref name="wide"/> puts the control under the text instead; <paramref name="extra"/>
    /// adds something below the whole card's first line (swatches, tiles, a dependent option).
    /// </summary>
    private static Border Card(string glyph, string title, string? description, FrameworkElement? control, bool wide = false, FrameworkElement? extra = null)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, Style = ThemedStyles.Text(caption: true, secondary: true), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            // Screen readers and UI Automation name a switch or a list by its card's title.
            if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control)))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
            if (wide)
            {
                text.Children.Add(control);
                control.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }
        }
        if (extra is not null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            extra.Margin = new Thickness(0, 12, 0, 2);
            Grid.SetRow(extra, 1);
            Grid.SetColumn(extra, 1);
            Grid.SetColumnSpan(extra, 2);
            grid.Children.Add(extra);
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Card(),
        };
    }

    // ----- section bar -----

    private void JumpToSection()
    {
        if (_jumping) return;
        var selected = _sections.SelectedItem;
        var anchor = _anchors.FirstOrDefault(a => a.Item == selected).Anchor;
        if (anchor is null || _scroller.Content is not UIElement content) return;
        var y = anchor.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        _jumping = true;
        _scroller.ChangeView(null, Math.Max(0, y - 8), null);
        _jumping = false;
    }

    /// <summary>The bar follows the scroll: the last section whose heading has passed the top.</summary>
    private void FollowScroll()
    {
        if (_jumping || _scroller.Content is not UIElement content) return;
        var top = _scroller.VerticalOffset + 40;
        var current = _anchors[0].Item;
        foreach (var (item, anchor) in _anchors)
            if (anchor.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y <= top) current = item;
        if (_scroller.VerticalOffset >= _scroller.ScrollableHeight - 1) current = _anchors[^1].Item;
        if (_sections.SelectedItem == current) return;
        _jumping = true;
        _sections.SelectedItem = current;
        _jumping = false;
    }

    // ----- appearance: preview, swatches, style tiles -----

    private static DownloadItem Sample(ulong id, string name, string state, double progress, long size, long speed = 0) =>
        DownloadItem.From(new JsonObject
        {
            ["id"] = (long)id,
            ["kind"] = "http",
            ["fileName"] = name,
            ["saveDir"] = "",
            ["state"] = state,
            ["totalSize"] = size,
            ["downloaded"] = (long)(size * progress),
            ["speed"] = speed,
        });

    /// <summary>A few rows drawn the way the list draws them, so every change shows here at once.</summary>
    private static Border Preview()
    {
        var rows = new StackPanel { Spacing = 4 };
        rows.Children.Add(PreviewRow(Sample(900001, "ubuntu-24.04.3-desktop-amd64.iso", "downloading", 0.64, 6_250_000_000, 18_400_000)));
        rows.Children.Add(PreviewRow(Sample(900002, "Big Buck Bunny 60fps 4K.mp4", "downloading", 0.37, 288_700_000, 9_200_000)));
        rows.Children.Add(PreviewRow(Sample(900003, "holiday-photos.zip", "paused", 0.48, 912_000_000)));
        rows.Children.Add(PreviewRow(Sample(900004, "quarterly-report.pdf", "finishing", 1, 4_200_000)));
        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Preview", Style = ThemedStyles.Text(caption: true, secondary: true) },
                    rows,
                },
            },
            Padding = new Thickness(16, 12, 16, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Card(),
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    /// <summary>The list's row fill (the same three layers the row template has), with text over it.</summary>
    private static Grid FillGrid(DownloadItem item, string? style = null)
    {
        var fill = new Grid { CornerRadius = new CornerRadius(4), IsHitTestVisible = false, DataContext = item, Tag = style };
        fill.Children.Add(new Border());
        fill.Children.Add(new Grid { Children = { new Border { Visibility = Visibility.Collapsed }, new Border { Visibility = Visibility.Collapsed } } });
        fill.Loaded += (_, _) => RowFill.Attach(fill);
        return fill;
    }

    private static Grid PreviewRow(DownloadItem item)
    {
        var row = new Grid { Height = 40, ColumnSpacing = 12, DataContext = item };
        row.Children.Add(FillGrid(item));
        var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        line.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 16 });
        var name = new TextBlock { Text = item.FileName, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        line.Children.Add(name);
        var size = new TextBlock { Text = item.SizeText, Style = ThemedStyles.Text(caption: false, secondary: true), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(size, 2);
        line.Children.Add(size);
        // The list's own Progress column: words, or the ring or bar.
        var progress = new ProgressCell();
        Grid.SetColumn(progress, 3);
        line.Children.Add(progress);
        row.Children.Add(line);
        return row;
    }

    // The swatches and the style tiles are GridViews: the selection, pointer-over and pressed looks,
    // the focus rectangle and arrow keys are the platform's. Changing a setting only moves the
    // selection; nothing is rebuilt, so focus and the scroll position stay where they are.

    /// <summary>A GridView that lays out its items but never scrolls; the page scrolls instead.</summary>
    private static GridView Picker(string name, bool followFocus)
    {
        var view = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            SingleSelectionFollowsFocus = followFocus,
            IsItemClickEnabled = false,
            Padding = new Thickness(0),
        };
        ScrollViewer.SetVerticalScrollMode(view, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(view, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(view, ScrollBarVisibility.Disabled);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(view, name);
        return view;
    }

    private bool _syncing;
    private GridViewItem? _customSwatch;

    private static Windows.UI.Color WindowsAccent => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    private void BuildSwatches()
    {
        _swatches.Items.Add(Swatch("system", "Windows accent", ""));
        foreach (var (name, hex) in Appearance.Presets)
            _swatches.Items.Add(Swatch(hex, name, null));

        // Any colour: a picker in a flyout, opened by choosing the last swatch.
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        var use = new Button { Content = "Use this colour", HorizontalAlignment = HorizontalAlignment.Right, Style = Res<Style>("AccentButtonStyle") };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 12, Children = { picker, use } } };
        use.Click += (_, _) =>
        {
            flyout.Hide();
            ViewSettings.Current.Accent = Appearance.ToHex(picker.Color);
        };
        _customSwatch = Swatch("custom", "Pick any colour", "");
        _swatches.Items.Add(_customSwatch);

        _swatches.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_swatches.SelectedItem is GridViewItem { Tag: "custom" } custom)
            {
                picker.Color = Appearance.CustomAccent ?? Appearance.TryParse("#0F6CBD")!.Value;
                flyout.ShowAt(custom);
            }
            else if (_swatches.SelectedItem is GridViewItem { Tag: string value }) ViewSettings.Current.Accent = value;
            // The selection shows the accent in use: the custom swatch only once a colour is picked.
            DispatcherQueue.TryEnqueue(SyncSwatches);
        };
    }

    /// <summary>A round colour swatch; the platform draws the selection ring around it, the check is ours.</summary>
    private static GridViewItem Swatch(string value, string name, string? glyph)
    {
        var dot = new Grid
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Get("Grid", null, ("BorderBrush", "ControlStrokeColorDefaultBrush")),
        };
        if (glyph is not null) dot.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Tag = "glyph" });
        dot.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = new SolidColorBrush(Colors.White), Visibility = Visibility.Collapsed, Tag = "check" });
        var item = new GridViewItem
        {
            Tag = value,
            Content = dot,
            Width = 40,
            Height = 40,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(20),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(item, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
        return item;
    }

    /// <summary>Selects the swatch in use and paints the dots; called after any accent change.</summary>
    private void SyncSwatches()
    {
        var current = ViewSettings.Current.Accent;
        var isCustom = current != "system" && !Appearance.Presets.Any(p => string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase));
        GridViewItem? selected = null;
        foreach (var item in _swatches.Items.OfType<GridViewItem>())
        {
            var value = (string)item.Tag;
            var dot = (Grid)item.Content;
            Windows.UI.Color? color = value switch
            {
                "system" => WindowsAccent,
                "custom" => isCustom ? Appearance.CustomAccent : null,
                _ => Appearance.TryParse(value),
            };
            dot.Background = color is { } c ? new SolidColorBrush(c) : null;
            var on = value == "custom" ? isCustom : string.Equals(value, current, StringComparison.OrdinalIgnoreCase);
            if (on) selected = item;
            foreach (var icon in dot.Children.OfType<FontIcon>())
            {
                if ((string)icon.Tag == "check") icon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                else
                {
                    icon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                    if (color is not null) icon.Foreground = new SolidColorBrush(Colors.White);
                    else icon.ClearValue(FontIcon.ForegroundProperty);
                }
            }
        }
        _syncing = true;
        _swatches.SelectedItem = selected;
        _syncing = false;
    }

    /// <summary>The shape tiles, then "Look" and the look tiles.</summary>
    private static StackPanel PickerPair(GridView shape, GridView look) => new()
    {
        Spacing = 4,
        Children =
        {
            shape,
            new TextBlock { Text = "Look", Style = ThemedStyles.Text(caption: true, secondary: true), Margin = new Thickness(0, 6, 0, 2) },
            look,
        },
    };

    private static DownloadItem TileSample(int i) => Sample(910000 + (ulong)i, "sample.iso", "downloading", 0.62, 100_000_000, 5_000_000);

    /// <summary>A small live row in the shape and look a Tag names, with its percentage over it.</summary>
    private static FrameworkElement RowSample(string tag, int i)
    {
        var item = TileSample(i);
        var sample = new Grid { Height = 34, DataContext = item };
        sample.Children.Add(FillGrid(item, tag));
        sample.Children.Add(new TextBlock { Text = "62%", FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sample;
    }

    /// <summary>A Progress column cell in the shape and look a Tag names.</summary>
    private static FrameworkElement ColumnSample(string tag, int i) => new Grid
    {
        Height = 34,
        DataContext = TileSample(i),
        Children = { new ProgressCell { Tag = tag, HorizontalAlignment = HorizontalAlignment.Stretch } },
    };

    private void BuildProgressPickers()
    {
        var v = ViewSettings.Current;
        // Shape tiles show the chosen look; look tiles show the chosen shape ("*" in the Tag).
        Fill(_rowShape, RowShapes, x => RowSample(x + ":*", 0), x => v.RowProgress = x, 3);
        Fill(_rowLook, Looks, x => RowSample("*:" + x, 1), x => v.RowLook = x, 4);
        Fill(_columnShape, ColumnShapes, x => ColumnSample(x + ":*", 2), x => v.ColumnProgress = x, 3);
        Fill(_columnLook, Looks, x => ColumnSample("*:" + x, 3), x => v.ColumnLook = x, 4);
    }

    private void Fill(GridView view, (string Value, string Label, string Hint)[] items, Func<string, FrameworkElement> sample, Action<string> choose, int columns)
    {
        foreach (var (value, label, hint) in items)
        {
            var tile = new GridViewItem
            {
                Tag = value,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(10, 10, 10, 8),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { sample(value), new TextBlock { Text = label, Style = ThemedStyles.Text(caption: true, secondary: false) } },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, label);
            ToolTipService.SetToolTip(tile, hint);
            view.Items.Add(tile);
        }
        view.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (view.SelectedItem is GridViewItem { Tag: string value }) choose(value);
            else DispatcherQueue.TryEnqueue(SyncProgressPickers);
        };
        // Equal columns that fill the card; half as many when narrow.
        void Size(double width)
        {
            if (width <= 0 || view.ItemsPanelRoot is not ItemsWrapGrid panel) return;
            var w = Math.Floor(width / (width >= 640 ? columns : Math.Max(2, columns / 2)));
            if (double.IsNaN(panel.ItemWidth) || Math.Abs(panel.ItemWidth - w) >= 1) panel.ItemWidth = w;
        }
        view.SizeChanged += (_, e) => Size(e.NewSize.Width);
        view.Loaded += (_, _) => Size(view.ActualWidth);
    }

    private void SyncProgressPickers()
    {
        var v = ViewSettings.Current;
        _syncing = true;
        foreach (var (view, value) in new[] { (_rowShape, v.RowProgress), (_rowLook, v.RowLook), (_columnShape, v.ColumnProgress), (_columnLook, v.ColumnLook) })
        {
            var tiles = view.Items.OfType<GridViewItem>().ToList();
            view.SelectedItem = tiles.FirstOrDefault(t => (string)t.Tag == value) ?? tiles[0];
        }
        _syncing = false;
        // A look only matters while its place shows progress.
        _rowLook.IsEnabled = v.RowProgress != "none";
        _columnLook.IsEnabled = v.ColumnProgress != "text";
    }

    private void OnViewSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewSettings.Accent)) SyncSwatches();
        else if (e.PropertyName is nameof(ViewSettings.RowProgress) or nameof(ViewSettings.RowLook)
            or nameof(ViewSettings.ColumnProgress) or nameof(ViewSettings.ColumnLook)) SyncProgressPickers();
    }

    private void Listen(bool on)
    {
        ViewSettings.Current.PropertyChanged -= OnViewSettingChanged;
        ThemeColors.Changed -= SyncSwatches;
        if (!on) return;
        ViewSettings.Current.PropertyChanged += OnViewSettingChanged;
        ThemeColors.Changed += SyncSwatches;
    }

    // ----- load and save -----

    private void Load()
    {
        var reply = _core.Call(new { type = "getSettings" });
        _settings = reply["settings"] as JsonObject ?? [];
        var folder = _settings["downloadDir"]?.GetValue<string>() ?? "";
        _folder.Text = string.IsNullOrEmpty(folder) ? reply["defaultDownloadDir"]?.GetValue<string>() ?? "" : folder;
        _categories.IsOn = _settings["categoryFolders"]?.GetValue<bool>() ?? true;
        _connections.Value = _settings["connections"]?.GetValue<int>() ?? 8;
        _parallel.Value = _settings["maxParallel"]?.GetValue<int>() ?? 3;
        var limit = _settings["speedLimit"]?.GetValue<long>() ?? 0;
        var inMb = limit == 0 || limit % (1024 * 1024) == 0;
        _speedUnit.SelectedIndex = inMb ? 0 : 1;
        _speed.Value = limit / (inMb ? 1024.0 * 1024 : 1024.0);
        var schedule = _settings["schedule"] as JsonObject;
        _schedule.IsOn = schedule?["enabled"]?.GetValue<bool>() ?? false;
        _start.Time = TimeSpan.TryParse(schedule?["start"]?.GetValue<string>(), out var a) ? a : new TimeSpan(23, 0, 0);
        _stop.Time = TimeSpan.TryParse(schedule?["stop"]?.GetValue<string>(), out var b) ? b : new TimeSpan(7, 0, 0);
        ShowQueueAction(_settings["whenDone"]?.GetValue<string>() ?? "nothing");
        _updateTools.IsOn = _settings["autoUpdateTools"]?.GetValue<bool>() ?? true;
        _confirm.IsOn = _settings["confirmBrowserDownloads"]?.GetValue<bool>() ?? true;

        _notifications.IsOn = _app.Notifications;
        _afterOpen.IsOn = _app.AfterOpen;
        _afterShow.IsOn = _app.AfterShowInFolder;
        _afterExtract.IsOn = _app.AfterExtract;
        _afterExtractDelete.IsChecked = _app.AfterExtractDelete;
        _afterExtractDelete.IsEnabled = _app.AfterExtract;
        _countdown.SelectedIndex = _app.QueueActionDelay switch { <= 10 => 0, <= 30 => 1, <= 60 => 2, _ => 3 };
        _once.IsOn = _app.QueueActionOnce;

        _progressWindows.IsOn = _app.ShowProgressWindow;
        var view = ViewSettings.Current;
        _theme.SelectedIndex = view.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _backdrop.SelectedIndex = view.Backdrop switch { "micaAlt" => 1, "acrylic" => 2, "solid" => 3, _ => 0 };
        _animation.SelectedIndex = view.Animation switch { "off" => 0, "lively" => 2, _ => 1 };
        _strength.SelectedIndex = view.FillStrength switch { "subtle" => 0, "vivid" => 2, _ => 1 };
        _colorBy.SelectedIndex = view.ColorBy == "type" ? 1 : 0;
        _density.SelectedIndex = view.Density switch { "compact" => 0, "comfortable" => 2, _ => 1 };
        _startWithWindows.IsOn = _app.StartWithWindows;
        _closeToTray.IsOn = _app.CloseToTray;
        _clipboard.IsOn = _app.WatchClipboard;
        ShowHostStatus();
        _toolsStatus.Text = File.Exists(Path.Combine(AppPaths.ToolsDir, "yt-dlp", "yt-dlp.exe")) || File.Exists(Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe"))
            ? "yt-dlp and ffmpeg are installed for video downloads."
            : "yt-dlp is missing, so video pages cannot be downloaded. Run scripts\\fetch-tools.ps1 when building from source.";
    }

    private void ShowQueueAction(string value)
    {
        var index = Array.FindIndex(AfterDownload.QueueActions, x => x.Value == value);
        _whenDone.SelectedIndex = Math.Max(0, index);
    }

    private void OnQueueActionChanged(string value) => DispatcherQueue.TryEnqueue(() =>
    {
        var was = _loading;
        _loading = true;
        ShowQueueAction(value);
        if (_settings is not null) _settings["whenDone"] = value;
        _loading = was;
    });

    private void Wire()
    {
        void Engine(object? s, object? e) { if (!_loading) _saveSoon.Restart(); }
        _folder.LostFocus += (_, _) => Engine(null, null);
        _categories.Toggled += Engine;
        _connections.ValueChanged += (_, _) => Engine(null, null);
        _parallel.ValueChanged += (_, _) => Engine(null, null);
        _speed.ValueChanged += (_, _) => Engine(null, null);
        _speedUnit.SelectionChanged += (_, _) => Engine(null, null);
        _schedule.Toggled += Engine;
        _start.TimeChanged += (_, _) => Engine(null, null);
        _stop.TimeChanged += (_, _) => Engine(null, null);
        _whenDone.SelectionChanged += (_, _) => Engine(null, null);
        _updateTools.Toggled += Engine;
        _confirm.Toggled += Engine;

        _notifications.Toggled += (_, _) => SaveApp(s => s.Notifications = _notifications.IsOn);
        _afterOpen.Toggled += (_, _) => SaveApp(s => s.AfterOpen = _afterOpen.IsOn);
        _afterShow.Toggled += (_, _) => SaveApp(s => s.AfterShowInFolder = _afterShow.IsOn);
        _afterExtract.Toggled += (_, _) =>
        {
            _afterExtractDelete.IsEnabled = _afterExtract.IsOn;
            SaveApp(s => s.AfterExtract = _afterExtract.IsOn);
        };
        _afterExtractDelete.Click += (_, _) => SaveApp(s => s.AfterExtractDelete = _afterExtractDelete.IsChecked == true);
        _countdown.SelectionChanged += (_, _) => SaveApp(s => s.QueueActionDelay = _countdown.SelectedIndex switch { 0 => 10, 2 => 60, 3 => 120, _ => 30 });
        _once.Toggled += (_, _) => SaveApp(s => s.QueueActionOnce = _once.IsOn);

        var view = ViewSettings.Current;
        _theme.SelectionChanged += (_, _) => { if (!_loading) view.Theme = _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; };
        _backdrop.SelectionChanged += (_, _) => { if (!_loading) view.Backdrop = _backdrop.SelectedIndex switch { 1 => "micaAlt", 2 => "acrylic", 3 => "solid", _ => "mica" }; };
        _animation.SelectionChanged += (_, _) => { if (!_loading) view.Animation = _animation.SelectedIndex switch { 0 => "off", 2 => "lively", _ => "calm" }; };
        _strength.SelectionChanged += (_, _) => { if (!_loading) view.FillStrength = _strength.SelectedIndex switch { 0 => "subtle", 2 => "vivid", _ => "normal" }; };
        _colorBy.SelectionChanged += (_, _) => { if (!_loading) view.ColorBy = _colorBy.SelectedIndex == 1 ? "type" : "status"; };
        _density.SelectionChanged += (_, _) => { if (!_loading) view.Density = _density.SelectedIndex switch { 0 => "compact", 2 => "comfortable", _ => "standard" }; };
        _progressWindows.Toggled += (_, _) => SaveApp(s => s.ShowProgressWindow = _progressWindows.IsOn);
        _startWithWindows.Toggled += (_, _) => SaveApp(s => s.StartWithWindows = _startWithWindows.IsOn, () => StartupRegistration.Apply(_startWithWindows.IsOn));
        _closeToTray.Toggled += (_, _) => SaveApp(s => s.CloseToTray = _closeToTray.IsOn);
        _clipboard.Toggled += (_, _) => SaveApp(s => s.WatchClipboard = _clipboard.IsOn);
    }

    private void SaveApp(Action<AppSettings> change, Action? then = null)
    {
        if (_loading) return;
        change(_app);
        try
        {
            _app.Save();
            then?.Invoke();
        }
        catch (Exception e)
        {
            ShowError($"Could not save the setting: {e.Message}");
        }
    }

    private async void SaveEngine()
    {
        static long N(NumberBox b, long fallback) => double.IsNaN(b.Value) ? fallback : (long)b.Value;
        // Only a full path is a usable folder; anything else keeps the saved one.
        var folder = _folder.Text.Trim();
        if (folder.Length > 0 && !IsFullPath(folder))
        {
            ShowError($"\"{folder}\" is not a full folder path, such as C:\\Users\\you\\Downloads. The download folder was not changed.");
            folder = _settings["downloadDir"]?.GetValue<string>() ?? "";
        }
        var whenDone = AfterDownload.QueueActions[Math.Max(0, _whenDone.SelectedIndex)].Value;
        var settings = new JsonObject(_settings.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
        {
            ["downloadDir"] = folder,
            ["categoryFolders"] = _categories.IsOn,
            ["connections"] = N(_connections, 8),
            ["maxParallel"] = N(_parallel, 3),
            ["speedLimit"] = (long)((double.IsNaN(_speed.Value) ? 0 : _speed.Value) * (_speedUnit.SelectedIndex == 0 ? 1024 * 1024 : 1024)),
            ["schedule"] = new JsonObject
            {
                ["enabled"] = _schedule.IsOn,
                ["start"] = _start.Time.ToString(@"hh\:mm"),
                ["stop"] = _stop.Time.ToString(@"hh\:mm"),
            },
            ["whenDone"] = whenDone,
            ["autoUpdateTools"] = _updateTools.IsOn,
            ["confirmBrowserDownloads"] = _confirm.IsOn,
        };
        var before = _settings["whenDone"]?.GetValue<string>();
        var reply = await _core.CallAsync(new { type = "setSettings", settings });
        if (reply["ok"]?.GetValue<bool>() == true && reply["settings"] is JsonObject saved) _settings = saved;
        else ShowError(reply["error"]?.GetValue<string>() ?? "Could not save the settings.");
        if (before != whenDone) AfterDownload.RaiseQueueActionChanged(whenDone);
    }

    private static bool IsFullPath(string path)
    {
        try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0; }
        catch { return false; }
    }

    private void ShowHostStatus() => _hostStatus.Text = NativeHostRegistration.IsRegistered()
        ? "Browsers with the extension can reach this app."
        : "The extension cannot reach this app yet. Connect again.";

    private void ShowError(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _folder.Text = folder.Path;
        _saveSoon.Restart();
    }

    /// <summary>Runs an action once, a moment after the last call: one save for a burst of changes.</summary>
    private sealed class DispatcherTimerLike
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherTimerLike(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan delay, Action action)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => action();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }
    }
}
