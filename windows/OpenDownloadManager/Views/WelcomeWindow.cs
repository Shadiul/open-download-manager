using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Core;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenDownloadManager.Views;

/// <summary>
/// Shown on the first start: an intro, then one choice per screen (light or dark, where files go
/// and starting with Windows, the extension in each browser), then a summary. The window keeps one
/// size; four short bars at the bottom show the steps, the current one sweeping like a running
/// download. Animation follows the app's setting and Windows' "Animation effects".
/// </summary>
public sealed class WelcomeWindow : ToolWindow
{
    private const int Steps = 4; // after the intro: Look, Folder, Browsers, Ready
    private const double ClientHeight = 500;

    private readonly CoreClient _core;
    private readonly AppSettings _settings;
    private readonly Grid _root = new() { Height = ClientHeight };
    private readonly ContentControl _host = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        IsTabStop = false,
    };
    // One spacing scale (4/8/12/16/24/32) and one left edge for every screen. The footer's Skip
    // button is pulled left by its own 11 px padding so its text lines up with the content.
    private const double Edge = 32;
    private readonly Grid _footer = new()
    {
        Padding = new Thickness(Edge - 11, 16, Edge, 20),
        ColumnSpacing = 16,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Style = ThemedStyles.Get("Grid", null, ("BorderBrush", "CardStrokeColorDefaultBrush"), ("Background", "CardBackgroundFillColorSecondaryBrush")),
    };
    private readonly Rectangle _tint = new() { IsHitTestVisible = false };
    private readonly List<Action> _repaint = []; // for the screen on show; cleared when it changes
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _poll;
    private readonly Dictionary<string, bool> _waiting = [];
    private List<Browser>? _browsers;
    private string _folder = "";
    private int _step;

    public WelcomeWindow(CoreClient core, AppSettings settings) : base("Welcome", 640)
    {
        _core = core;
        _settings = settings;
        ReadFolder();

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRowSpan(_tint, 2);
        _root.Children.Add(_tint);
        _root.Children.Add(_host);
        Grid.SetRow(_footer, 1);
        _root.Children.Add(_footer);
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Content = _root;

        // A faint wash of the accent over the window material.
        PaintTint();
        ThemeColors.Changed += OnThemeChanged;

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(1.5);
        _poll.Tick += (_, _) => CheckBrowsers();

        Closed += (_, _) =>
        {
            ThemeColors.Changed -= OnThemeChanged;
            _poll.Stop();
            MarkDone();
        };
        Show(0);
        Ready();
    }

    // ----- motion -----

    /// <summary>Off when the app's animation setting is off, or Windows' animation effects are.</summary>
    private static bool Motion => ViewSettings.Current.Animation != "off" && new UISettings().AnimationsEnabled;

    private static bool Lively => ViewSettings.Current.Animation == "lively";

    // Fluent's decelerate curve. A KeySpline belongs to one key frame, so each gets its own.
    private static KeySpline Decelerate => new() { ControlPoint1 = new Windows.Foundation.Point(0.1, 0.9), ControlPoint2 = new Windows.Foundation.Point(0.2, 1) };

    private static CompositeTransform Transform(UIElement e)
    {
        if (e.RenderTransform is CompositeTransform t) return t;
        t = new CompositeTransform();
        e.RenderTransform = t;
        e.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return t;
    }

    /// <summary>Adds an eased from→to animation of one property to the storyboard.</summary>
    private static void Add(Storyboard sb, DependencyObject target, string property, double from, double to, double ms, double delay = 0)
    {
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delay) };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        a.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Value = to, KeySpline = Decelerate });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
    }

    private static void Forever(DependencyObject target, string property, double from, double to, double seconds, bool reverse = false)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = reverse,
            EnableDependentAnimation = false,
        };
        if (reverse) a.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        var sb = new Storyboard();
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
        sb.Begin();
    }

    /// <summary>Children slide up into place one after another when the screen appears.</summary>
    private static void Stagger(Panel panel)
    {
        if (!Motion) return;
        panel.ChildrenTransitions = [new EntranceThemeTransition { IsStaggeringEnabled = true, FromVerticalOffset = Lively ? 28 : 16 }];
    }

    // ----- colour helpers -----

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Runs now and again whenever light/dark or the accent changes.</summary>
    private void Repaint(Action paint)
    {
        _repaint.Add(paint);
        paint();
    }

    private void OnThemeChanged()
    {
        PaintTint();
        foreach (var paint in _repaint.ToList()) paint();
    }

    private void PaintTint() => _tint.Fill = new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Color = WithAlpha(ThemeColors.Accent, ThemeColors.IsDark ? (byte)0x1C : (byte)0x14), Offset = 0 },
            new GradientStop { Color = WithAlpha(ThemeColors.Accent, 0), Offset = 0.75 },
        },
    };

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static Style AccentText(string type = "TextBlock") => ThemedStyles.Get(type, null, ("Foreground", "AccentTextFillColorPrimaryBrush"));

    private static FontIcon Glyph(string glyph, double size) => new() { Glyph = glyph, FontSize = size, Style = AccentText("FontIcon") };

    /// <summary>A rounded square tinted with the accent, holding a glyph.</summary>
    private static Grid Tile(string glyph, double size, double radius, double glyphSize) => new()
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Border
            {
                CornerRadius = new CornerRadius(radius),
                Opacity = 0.16,
                Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush")),
            },
            new Border
            {
                CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(1),
                Opacity = 0.3,
                Style = ThemedStyles.Get("Border", null, ("BorderBrush", "AccentFillColorDefaultBrush")),
            },
            new ContentPresenter { Content = Glyph(glyph, glyphSize), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    private static Border Card(UIElement child, Thickness padding) => new()
    {
        Child = child,
        Padding = padding,
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Style = ThemedStyles.Card(),
    };

    private static TextBlock Text(string text, bool caption = false, bool secondary = false) => new()
    {
        Text = text,
        Style = ThemedStyles.Text(caption, secondary),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Image AppArt(double size) => new()
    {
        Source = new BitmapImage(new Uri(AppPaths.IconPngPath)) { DecodePixelWidth = (int)Math.Ceiling(size * 2) },
        Width = size,
        Height = size,
    };

    // ----- screens -----

    private void Show(int step)
    {
        _step = step;
        _poll.Stop();
        _repaint.Clear();
        FrameworkElement screen = step switch
        {
            0 => IntroScreen(),
            1 => LookScreen(),
            2 => FolderScreen(),
            3 => BrowsersScreen(),
            _ => ReadyScreen(),
        };
        _host.Content = screen;
        BuildFooter();
        if (step == 3) _poll.Start();
    }

    /// <summary>Straight to the browsers step, for the installer's "Set up the browser extension".</summary>
    public void ShowBrowsers() => Show(3);

    private void BuildFooter()
    {
        _footer.Children.Clear();
        var primary = new Button
        {
            Content = _step switch { 0 => "Get started", 3 when !AnyAdded() => "Not now", Steps => "Start", _ => "Next" },
            Style = Res<Style>("AccentButtonStyle"),
            MinWidth = _step is 0 or Steps ? 132 : 96,
        };
        if (_step is 0 or Steps) primary.Padding = new Thickness(22, 7, 22, 8);
        primary.Click += (_, _) => { if (_step == Steps) Finish(); else Show(_step + 1); };

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (_step > 0)
        {
            var back = new Button { Content = "Back", MinWidth = 96 };
            back.Click += (_, _) => Show(_step - 1);
            right.Children.Add(back);
        }
        right.Children.Add(primary);
        Grid.SetColumn(right, 2);
        _footer.Children.Add(right);

        // On the last screen Skip is kept invisible, so the bars stay where they were.
        {
            var skip = new Button
            {
                Content = "Skip setup",
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Style = ThemedStyles.Get("Button", "DefaultButtonStyle", ("Foreground", "TextFillColorSecondaryBrush")),
            };
            skip.Click += (_, _) => Finish();
            if (_step == Steps)
            {
                skip.Opacity = 0;
                skip.IsHitTestVisible = false;
                skip.IsTabStop = false;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(skip, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            }
            _footer.Children.Add(skip);
        }

        if (_step > 0)
        {
            var bars = Progress();
            Grid.SetColumn(bars, 1);
            _footer.Children.Add(bars);
        }
        primary.Loaded += (_, _) => primary.Focus(FocusState.Programmatic);
    }

    /// <summary>Four short bars: done ones filled, the current one sweeping like a running download.</summary>
    private StackPanel Progress()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, $"Step {_step} of {Steps}");
        for (var i = 1; i <= Steps; i++)
        {
            var done = i < _step || _step == Steps;
            var current = i == _step && _step < Steps;
            var bar = new Grid { Width = 34, Height = 4, CornerRadius = new CornerRadius(2) };
            bar.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 34, 4) };
            var track = new Border();
            if (done) track.Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush"));
            else if (current) { track.Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush")); track.Opacity = 0.35; }
            else track.Style = ThemedStyles.Get("Border", null, ("Background", "ControlStrongFillColorDisabledBrush"));
            bar.Children.Add(track);
            if (current && Motion)
            {
                var band = new Rectangle { Width = 22, HorizontalAlignment = HorizontalAlignment.Left };
                Repaint(() =>
                {
                    var c = ThemeColors.IsDark ? ThemeColors.AccentShade(UIColorType.AccentLight2) : ThemeColors.AccentShade(UIColorType.Accent);
                    band.Fill = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0.5),
                        EndPoint = new Windows.Foundation.Point(1, 0.5),
                        GradientStops =
                        {
                            new GradientStop { Color = WithAlpha(c, 0), Offset = 0 },
                            new GradientStop { Color = c, Offset = 0.5 },
                            new GradientStop { Color = WithAlpha(c, 0), Offset = 1 },
                        },
                    };
                });
                bar.Children.Add(band);
                Forever(Transform(band), nameof(CompositeTransform.TranslateX), -22, 34, Lively ? 1.1 : 1.9);
            }
            panel.Children.Add(bar);
        }
        return panel;
    }

    /// <summary>The glyph tile, "Step n of 4" and the question, with an optional line under it.</summary>
    private static StackPanel Head(int step, string glyph, string question, string? detail)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = Tile(glyph, 48, 12, 22);
        tile.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(tile);
        var words = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = $"Step {step} of {Steps}", FontSize = 12, FontWeight = FontWeights.SemiBold, Style = AccentText() },
                new TextBlock
                {
                    Text = question,
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = 32,
                    TextWrapping = TextWrapping.Wrap,
                    Style = ThemedStyles.Text(),
                },
            },
        };
        if (detail is not null)
        {
            var line = Text(detail, secondary: true);
            line.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(line);
        }
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        // 16 between children plus 8 here: the header sits 24 above the first card.
        return new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { grid } };
    }

    /// <summary>A screen's body: scrolls if a long browser list needs it.</summary>
    private static ScrollViewer Body(StackPanel content)
    {
        content.Padding = new Thickness(Edge, 24, Edge, 24);
        content.Spacing = 16;
        Stagger(content);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
    }

    // Intro: the artwork in a slow accent glow with an orbit, the name, and three things the app does.
    private Grid IntroScreen()
    {
        var aura = new Ellipse { Width = 260, Height = 260, IsHitTestVisible = false };
        Repaint(() =>
        {
            var a = ThemeColors.AccentShade(UIColorType.Accent);
            aura.Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(a, ThemeColors.IsDark ? (byte)0x70 : (byte)0x58), Offset = 0 },
                    new GradientStop { Color = WithAlpha(a, ThemeColors.IsDark ? (byte)0x26 : (byte)0x1C), Offset = 0.5 },
                    new GradientStop { Color = WithAlpha(a, 0), Offset = 1 },
                },
            };
        });

        var orbit = new Grid { Width = 184, Height = 184, IsHitTestVisible = false };
        orbit.Children.Add(new Ellipse
        {
            StrokeThickness = 1.2,
            StrokeDashArray = [1.5, 5],
            Opacity = 0.35,
            Style = ThemedStyles.Get("Ellipse", null, ("Stroke", "TextFillColorTertiaryBrush")),
        });
        orbit.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, 0, 0),
            Style = ThemedStyles.Get("Ellipse", null, ("Fill", "AccentFillColorDefaultBrush")),
        });
        orbit.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-2.5, 0, 0, 0),
            Opacity = 0.35,
            Style = ThemedStyles.Get("Ellipse", null, ("Fill", "TextFillColorPrimaryBrush")),
        });

        var art = AppArt(124);
        var hero = new Grid { Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Center, Children = { aura, orbit, art } };
        aura.Margin = new Thickness(-30);

        var title = new TextBlock
        {
            Text = "Welcome to Open Download Manager",
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
            Style = ThemedStyles.Text(),
        };
        var line = Text("A free, open-source download manager for Windows. Let's set it up: four quick steps.", secondary: true);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.TextAlignment = TextAlignment.Center;

        var feats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (glyph, text) in new[] { ("", "Pause and resume"), ("", "Videos from the sites you use"), ("", "Browser downloads") })
        {
            var dot = Tile(glyph, 22, 11, 11);
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Style = ThemedStyles.Text() } } };
            feats.Children.Add(new Border
            {
                Child = chip,
                Padding = new Thickness(4, 4, 12, 4),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Style = ThemedStyles.Card(),
            });
        }

        var words = new StackPanel { Spacing = 8, Children = { title, line, feats } };
        Stagger(words);
        var screen = new Grid { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(Edge, 0, Edge, 8) };
        screen.Children.Add(new StackPanel { Children = { hero, words } });

        if (Motion)
        {
            Forever(Transform(orbit), nameof(CompositeTransform.Rotation), 0, 360, Lively ? 14 : 28);
            var at = Transform(aura);
            Forever(at, nameof(CompositeTransform.ScaleX), 0.94, 1.06, 4.5, reverse: true);
            Forever(at, nameof(CompositeTransform.ScaleY), 0.94, 1.06, 4.5, reverse: true);
            var t = Transform(art);
            var sb = new Storyboard();
            Add(sb, art, "Opacity", 0, 1, 700);
            Add(sb, t, nameof(CompositeTransform.TranslateY), 26, 0, 1000);
            Add(sb, t, nameof(CompositeTransform.ScaleX), 0.9, 1, 1000);
            Add(sb, t, nameof(CompositeTransform.ScaleY), 0.9, 1, 1000);
            Add(sb, aura, "Opacity", 0, 1, 1400);
            sb.Begin();
        }
        return screen;
    }

    private static readonly string[] ThemeNames = ["Match Windows", "Light", "Dark"];
    private static readonly string[] ThemeKeys = ["system", "light", "dark"];

    private static int ThemeIndex => Math.Max(0, Array.IndexOf(ThemeKeys, ViewSettings.Current.Theme));

    // Look: three cards, each a tiny app window; picking one turns this window that way at once.
    private ScrollViewer LookScreen()
    {
        var cards = new Grid { ColumnSpacing = 12 };
        var outlines = new List<Border>();
        var radios = new List<RadioButton>();
        for (var i = 0; i < 3; i++)
        {
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var radio = new RadioButton { Content = ThemeNames[i], GroupName = "welcomeTheme", IsChecked = ThemeIndex == i, MinWidth = 0, Margin = new Thickness(4, 0, 0, 0) };
            radio.Checked += (_, _) =>
            {
                ViewSettings.Current.Theme = ThemeKeys[index];
                for (var k = 0; k < outlines.Count; k++) Outline(outlines[k], k == index);
            };
            radios.Add(radio);
            var outline = new Border
            {
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(10),
                Child = new StackPanel { Spacing = 8, Children = { MiniWindow(i), radio } },
            };
            outline.Tapped += (_, _) => radio.IsChecked = true;
            Outline(outline, ThemeIndex == i);
            outlines.Add(outline);
            Grid.SetColumn(outline, i);
            cards.Children.Add(outline);
        }

        var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Style = ThemedStyles.Get("FontIcon", null, ("Foreground", "TextFillColorSecondaryBrush")) });
        note.Children.Add(Text("The accent colour, window material and row style are in Settings, under Appearance.", caption: true, secondary: true));

        return Body(new StackPanel { Children = { Head(1, "", "How should it look?", null), cards, note } });
    }

    private static void Outline(Border card, bool selected)
    {
        card.BorderThickness = new Thickness(selected ? 2 : 1);
        card.Margin = new Thickness(selected ? 0 : 1);
        card.Style = selected
            ? ThemedStyles.Get("Border", null, ("BorderBrush", "AccentFillColorDefaultBrush"), ("Background", "CardBackgroundFillColorSecondaryBrush"))
            : ThemedStyles.Card(secondary: true);
    }

    /// <summary>A small picture of the app's window, light, dark, or split for Match Windows.</summary>
    private static FrameworkElement MiniWindow(int kind)
    {
        const double w = 156, h = 120;
        var accent = ThemeColors.AccentShade(UIColorType.Accent);
        var frame = new Grid { Width = w, Height = h, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center };
        frame.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) };
        frame.Children.Add(Face(dark: kind == 2, accent, w, h));
        if (kind == 0)
        {
            // The dark half on a slant: a rotated, clipped strip holding a counter-rotated copy.
            const double angle = 18;
            var strip = new Canvas { Width = w, Height = h * 2, IsHitTestVisible = false };
            strip.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h * 2) };
            var inner = Face(dark: true, accent, w, h);
            inner.RenderTransform = new RotateTransform { Angle = -angle, CenterX = 0, CenterY = 0 };
            strip.Children.Add(inner);
            var holder = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
            Canvas.SetLeft(strip, w * 0.62);
            Canvas.SetTop(strip, -h * 0.25);
            strip.RenderTransform = new RotateTransform { Angle = angle };
            // Position the copy so it lines up with the face underneath.
            var rad = angle * Math.PI / 180;
            var x0 = -w * 0.62; var y0 = h * 0.25;
            Canvas.SetLeft(inner, x0 * Math.Cos(rad) + y0 * Math.Sin(rad));
            Canvas.SetTop(inner, -x0 * Math.Sin(rad) + y0 * Math.Cos(rad));
            holder.Children.Add(strip);
            frame.Children.Add(holder);
        }
        frame.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)), IsHitTestVisible = false });
        return frame;
    }

    private static Grid Face(bool dark, Color accent, double w, double h)
    {
        Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var bg = dark ? C(0xFF1F2023) : C(0xFFEEF0F3);
        var row = dark ? C(0xFF2B2C30) : C(0xFFFFFFFF);
        var line = dark ? C(0xFF46484D) : C(0xFFD5D8DD);
        var tool = dark ? C(0xFF33353A) : C(0xFFE2E5EA);
        var accentButton = dark ? Appearance.Shade(accent, UIColorType.AccentLight2) : accent;

        var face = new Grid { Width = w, Height = h, Background = new SolidColorBrush(bg) };
        var stack = new StackPanel { Spacing = 4, Padding = new Thickness(6, 5, 6, 0) };
        Border Bar(double width, double height, Color c) => new() { Width = width, Height = height, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Bar(7, 7, accent), Bar(30, 3, line) } });
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 2), Children = { Bar(28, 9, accentButton), Bar(16, 9, tool), Bar(16, 9, tool) } });
        foreach (var (icon, fill, width) in new[] { (C(0xFFE8A33D), 0.64, 44.0), (C(0xFF4C8BF5), 0.28, 34.0), (C(0xFFD9534F), 0.0, 50.0), (C(0xFF5CB85C), 0.0, 38.0) })
        {
            var r = new Grid { Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(row) };
            if (fill > 0) r.Children.Add(new Border { Width = (w - 12) * fill, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(WithAlpha(accent, 0x38)) });
            r.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(5, 0, 5, 0),
                Children = { Bar(9, 11, icon), Bar(width, 3, line) },
            });
            r.Children.Add(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(line), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) });
            stack.Children.Add(r);
        }
        face.Children.Add(new Border { Background = new SolidColorBrush(WithAlpha(accent, 0x10)) });
        face.Children.Add(stack);
        return face;
    }

    // Folder: where downloads go (with the drive's free space) and starting with Windows.
    private ScrollViewer FolderScreen()
    {
        var change = new Button { Content = "Change…", VerticalAlignment = VerticalAlignment.Center };
        var path = new TextBlock { Text = _folder, FontSize = 15, Style = ThemedStyles.Text(), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(path, _folder);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 8, Children = { path } };
        if (FreeSpace(_folder) is var (free, total))
        {
            var used = total > 0 ? Math.Clamp(1 - (double)free / total, 0, 1) : 0;
            var bar = new Grid { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(2) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - used, GridUnitType.Star) });
            var track = new Border { Style = ThemedStyles.Get("Border", null, ("Background", "ControlStrongFillColorDisabledBrush")) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush")) });
            info.Children.Add(bar);
            info.Children.Add(Text($"{Size(free)} free of {Size(total)}", caption: true, secondary: true));
        }
        change.Click += async (_, _) => { if (await PickFolder()) Show(2); };
        var folderCard = Card(Row(Tile("", 40, 8, 18), info, change), new Thickness(16));

        var startup = new ToggleSwitch { IsOn = _settings.StartWithWindows, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(startup, "Start with Windows");
        startup.Toggled += (_, _) =>
        {
            _settings.StartWithWindows = startup.IsOn;
            try { _settings.Save(); StartupRegistration.Apply(startup.IsOn); } catch { /* shown in Settings */ }
        };
        var startupText = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Children = { Text("Start with Windows"), Text("It waits in the tray, so a download from your browser always has somewhere to go.", caption: true, secondary: true) },
        };
        var startupCard = Card(Row(Tile("", 40, 8, 18), startupText, startup), new Thickness(16, 16, 4, 16));

        return Body(new StackPanel
        {
            Children =
            {
                Head(2, "", "Where should downloads go?", "Every download is saved here, unless you choose another folder for it."),
                folderCard,
                startupCard,
            },
        });
    }

    private static Grid Row(UIElement left, UIElement middle, UIElement? right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn((FrameworkElement)middle, 1);
        grid.Children.Add(middle);
        if (right is FrameworkElement r)
        {
            Grid.SetColumn(r, 2);
            grid.Children.Add(r);
        }
        return grid;
    }

    private static (long Free, long Total)? FreeSpace(string folder)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch
        {
            return null; // a network path or a drive that is gone: just show the path
        }
    }

    private static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u >= 4 ? $"{v:0.##} {units[u]}" : $"{v:0} {units[u]}";
    }

    // Browsers: each one with Add, a waiting state while its guide is open, and a clear Added.
    private ScrollViewer BrowsersScreen()
    {
        _browsers = Browsers.Installed();
        var list = new StackPanel { Spacing = 8 };
        if (_browsers.Count == 0)
            list.Children.Add(Text("No supported browser found. The extension works in Edge, Chrome, Brave, Vivaldi and Opera. You can add it later from Settings.", secondary: true));
        else if (!BundledExtension.IsBundled)
            list.Children.Add(Text("This copy of the app was built without the extension. Build it with node scripts/build.mjs, or get it from the release page.", secondary: true));
        foreach (var browser in _browsers) list.Children.Add(BrowserRow(browser, justAdded: false));

        var note = new Grid { ColumnSpacing = 8 };
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        note.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0), Style = ThemedStyles.Get("FontIcon", null, ("Foreground", "TextFillColorSecondaryBrush")) });
        var noteText = Text("Add opens a short guide next to this window: the browser's extensions page, and two clicks there. This page notices when it's done.", caption: true, secondary: true);
        Grid.SetColumn(noteText, 1);
        note.Children.Add(noteText);

        return Body(new StackPanel
        {
            Children =
            {
                Head(3, "", "Catch downloads from your browser", "The extension comes with the app. Add it, and the files and videos you download in that browser come here."),
                list,
                note,
            },
        });
    }

    private Border BrowserRow(Browser browser, bool justAdded)
    {
        var shortName = browser.Name.Split(' ')[^1];
        var added = browser.HasExtension();
        if (added) _waiting.Remove(browser.Id);
        var waiting = !added && _waiting.ContainsKey(browser.Id);

        var icon = new Image { Width = 32, Height = 32 };
        _ = LoadIcon(icon, browser.Exe);
        var iconBox = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, Children = { icon } };
        if (added)
        {
            var badge = new Grid
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -3, -3),
                Children =
                {
                    new Ellipse { Style = ThemedStyles.Get("Ellipse", null, ("Fill", "SolidBackgroundFillColorBaseBrush")), Margin = new Thickness(-2) },
                    new Ellipse { Fill = ThemeColors.Brush(() => ThemeColors.Success, "success") },
                    new FontIcon { Glyph = "", FontSize = 10, Foreground = new SolidColorBrush(ThemeColors.IsDark ? Colors.Black : Colors.White) },
                },
            };
            iconBox.Children.Add(badge);
            if (justAdded && Motion)
            {
                var t = Transform(badge);
                var sb = new Storyboard();
                Add(sb, t, nameof(CompositeTransform.ScaleX), 0, 1, 450);
                Add(sb, t, nameof(CompositeTransform.ScaleY), 0, 1, 450);
                sb.Begin();
            }
        }

        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (waiting) sub.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0 });
        sub.Children.Add(Text(added ? $"Downloads from {shortName} come here" : waiting ? $"Waiting for {shortName}… finish the two steps in the guide" : "Not added yet", caption: true, secondary: true));
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Children = { Text(browser.Name), sub } };

        FrameworkElement right;
        if (added)
        {
            right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                MinWidth = 96,
                Margin = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 12, Foreground = ThemeColors.Brush(() => ThemeColors.Success, "success") },
                    new TextBlock { Text = "Added", FontWeight = FontWeights.SemiBold, Foreground = ThemeColors.Brush(() => ThemeColors.Success, "success") },
                },
            };
        }
        else
        {
            var button = new Button { Content = waiting ? "Show steps" : "Add", MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, IsEnabled = BundledExtension.IsBundled };
            if (!waiting) button.Style = Res<Style>("AccentButtonStyle");
            button.Click += (_, _) =>
            {
                _waiting[browser.Id] = true;
                ExtensionGuideWindow.Show(browser);
                RefreshBrowsers(null);
            };
            right = button;
        }

        var row = Card(Row(iconBox, words, right), new Thickness(16));
        row.Tag = browser.Id;
        if (added) row.BorderBrush = ThemeColors.Brush(() => ThemeColors.Success, "success", 0x70);
        return row;
    }

    private static async Task LoadIcon(Image image, string exe) => image.Source = await FileIcons.ForAsync(exe, exe, 64);

    private bool AnyAdded() => (_browsers ??= Browsers.Installed()).Any(b => b.HasExtension());

    /// <summary>Redraws the browser rows in place (the screen stays, so nothing animates in again).</summary>
    private void RefreshBrowsers(string? justAdded)
    {
        if (_step != 3 || _host.Content is not ScrollViewer { Content: StackPanel body } || _browsers is null) return;
        var list = body.Children.OfType<StackPanel>().FirstOrDefault(p => p.Children.OfType<Border>().Any(b => b.Tag is string));
        if (list is null) return;
        var transitions = list.ChildrenTransitions;
        list.ChildrenTransitions = null;
        for (var i = 0; i < list.Children.Count; i++)
            if (list.Children[i] is Border { Tag: string id } && _browsers.FirstOrDefault(b => b.Id == id) is { } browser)
                list.Children[i] = BrowserRow(browser, justAdded == id);
        list.ChildrenTransitions = transitions;
        BuildFooter();
    }

    private void CheckBrowsers()
    {
        if (_browsers is null) return;
        foreach (var id in _waiting.Keys.ToList())
            if (_browsers.FirstOrDefault(b => b.Id == id) is { } b && b.HasExtension())
                RefreshBrowsers(id);
    }

    // Ready: a short lift-off, what was chosen (each with a way back), and one tip.
    private ScrollViewer ReadyScreen()
    {
        var glow = new Ellipse { Width = 100, Height = 100, Margin = new Thickness(-14), IsHitTestVisible = false };
        Repaint(() =>
        {
            var a = ThemeColors.AccentShade(UIColorType.Accent);
            glow.Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(a, 0x50), Offset = 0 },
                    new GradientStop { Color = WithAlpha(a, 0), Offset = 1 },
                },
            };
        });
        var art = AppArt(72);
        var launch = new Grid { Width = 72, Height = 72, Children = { glow, art } };
        if (Motion)
        {
            var t = Transform(art);
            var sb = new Storyboard();
            Add(sb, art, "Opacity", 0, 1, 500);
            Add(sb, t, nameof(CompositeTransform.TranslateX), -10, 0, 900);
            Add(sb, t, nameof(CompositeTransform.TranslateY), 18, 0, 900);
            Add(sb, t, nameof(CompositeTransform.ScaleX), 0.85, 1, 900);
            Add(sb, t, nameof(CompositeTransform.ScaleY), 0.85, 1, 900);
            Add(sb, glow, "Opacity", 0, 1, 1000);
            sb.Begin();
        }
        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "You're ready to go", FontFamily = new FontFamily("Segoe UI Variable Display"), FontSize = 28, FontWeight = FontWeights.SemiBold, Style = ThemedStyles.Text() },
                Text("Here's how it's set up. You can change any of it later in Settings.", secondary: true),
            },
        };
        var head = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(launch);
        Grid.SetColumn(title, 1);
        head.Children.Add(title);

        var added = (_browsers ??= Browsers.Installed()).Where(b => b.HasExtension()).Select(b => b.Name.Split(' ')[^1]).ToList();
        var summary = new StackPanel();
        void Item(string label, string value, int step)
        {
            var check = Tile("", 20, 10, 10);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = ThemedStyles.Text(), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + " " });
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value });
            ((Microsoft.UI.Xaml.Documents.Run)text.Inlines[0]).Foreground = ThemeColors.Brush(() => ThemeColors.SecondaryText, "secondaryText");
            ToolTipService.SetToolTip(text, value);
            var change = new HyperlinkButton { Content = "Change", FontSize = 12, Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(change, $"Change {label.TrimEnd(':')}");
            change.Click += (_, _) => Show(step);
            var row = Row(check, text, change);
            row.ColumnSpacing = 12;
            var cell = new Border { Child = row, Padding = new Thickness(16, 8, 10, 8) };
            if (summary.Children.Count > 0)
            {
                cell.BorderThickness = new Thickness(0, 1, 0, 0);
                cell.Style = ThemedStyles.Get("Border", null, ("BorderBrush", "CardStrokeColorDefaultBrush"));
            }
            summary.Children.Add(cell);
        }
        Item("Look:", ThemeNames[ThemeIndex], 1);
        Item("Saves to:", _folder, 2);
        Item("Start with Windows:", _settings.StartWithWindows ? "On" : "Off", 2);
        Item("Browser extension:", added.Count > 0 ? "Added to " + string.Join(", ", added) : "Not added yet", 3);

        var tipText = Text("Copy a link to a file or a video page, anywhere, and Open Download Manager offers to download it. In the app, Ctrl+N starts a download from a link.");
        tipText.FontSize = 13;
        var tip = new Grid { ColumnSpacing = 12 };
        tip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tip.Children.Add(new ContentPresenter { Content = Glyph("", 16), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(tipText, 1);
        tip.Children.Add(tipText);
        var tipCard = new Grid
        {
            Children =
            {
                new Border { CornerRadius = new CornerRadius(8), Opacity = 0.1, Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush")) },
                new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Opacity = 0.25, Style = ThemedStyles.Get("Border", null, ("BorderBrush", "AccentFillColorDefaultBrush")) },
                new Border { Padding = new Thickness(16, 12, 16, 12), Child = tip },
            },
        };

        return Body(new StackPanel { Children = { head, Card(summary, new Thickness(0)), tipCard } });
    }

    // ----- settings -----

    private void ReadFolder()
    {
        var reply = _core.Call(new { type = "getSettings" });
        var folder = reply["settings"]?["downloadDir"]?.GetValue<string>();
        _folder = string.IsNullOrEmpty(folder) ? reply["defaultDownloadDir"]?.GetValue<string>() ?? "" : folder;
    }

    private async Task<bool> PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return false;
        var reply = await _core.CallAsync(new { type = "getSettings" });
        if (reply["settings"] is not JsonObject settings) return false;
        settings["downloadDir"] = folder.Path;
        await _core.CallAsync(new { type = "setSettings", settings });
        ReadFolder();
        return true;
    }

    private void Finish()
    {
        MarkDone();
        Close();
    }

    private void MarkDone()
    {
        if (_settings.WelcomeDone) return;
        _settings.WelcomeDone = true;
        try { _settings.Save(); } catch { /* shown again next time; harmless */ }
    }
}
