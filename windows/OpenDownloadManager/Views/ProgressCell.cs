using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.UI;

namespace OpenDownloadManager.Views;

/// <summary>
/// The Progress column of a row. Most row styles draw progress across the whole row (RowFill) and
/// this cell only says it in words: a state glyph and "36%", "Paused 36%", "Done". Two styles draw
/// it here instead:
/// <list type="bullet">
/// <item><b>ring</b>: a small circle that fills clockwise, with the percentage or state beside it;
/// a spinning arc while the size is unknown.</item>
/// <item><b>bar</b>: a thin bar across the column under the progress text.</item>
/// </list>
/// Colours follow the state (accent, amber when paused, red when failed, green when done) or the
/// file type, and the fill strength. Motion follows the Animation setting and Windows' "Animation
/// effects". The row template's DataContext is the download; a cell whose Tag names a style
/// (Settings' previews) keeps that style.
/// </summary>
public sealed partial class ProgressCell : Grid
{
    // Words: the default for every style but the two below.
    private readonly StackPanel _words = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _glyph = new() { FontSize = 12 };
    private readonly TextBlock _text = Cell();

    // Ring.
    private readonly StackPanel _ringRow = new() { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
    private readonly Grid _ring = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _track = new();
    private readonly Microsoft.UI.Xaml.Shapes.Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _ringText = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly FontIcon _ringGlyph = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _ringLabel = Cell();

    // Column bar.
    private readonly StackPanel _barStack = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
    private readonly TextBlock _barText = Cell();
    private readonly Grid _bar = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _barFill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _barGlint = new() { Width = 24, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };

    private DownloadItem? _item;
    private bool _live;
    private string _spinning = "";   // what is moving now: "", "ring", "bar", "glint"
    private float _spinWidth;

    public ProgressCell()
    {
        VerticalAlignment = VerticalAlignment.Stretch;
        _words.Children.Add(_glyph);
        _words.Children.Add(_text);

        _ring.Children.Add(_track);
        _ring.Children.Add(_arc);
        _ring.Children.Add(_ringText);
        _ring.Children.Add(_ringGlyph);
        _ringRow.Children.Add(_ring);
        _ringRow.Children.Add(_ringLabel);

        _barText.HorizontalAlignment = HorizontalAlignment.Left;
        _barFill.Child = new Grid { Children = { _barGlint } };
        _bar.Children.Add(_barFill);
        _barStack.Children.Add(_barText);
        _barStack.Children.Add(_bar);

        Children.Add(_words);
        Children.Add(_ringRow);
        Children.Add(_barStack);

        ElementCompositionPreview.SetIsTranslationEnabled(_barFill, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_barGlint, true);
        // The fill and the glint never leave the bar.
        var barVisual = ElementCompositionPreview.GetElementVisual(_bar);
        barVisual.Clip = barVisual.Compositor.CreateInsetClip();
        var fillVisual = ElementCompositionPreview.GetElementVisual(_barFill);
        fillVisual.Clip = fillVisual.Compositor.CreateInsetClip();

        Loaded += (_, _) => Resume();
        Unloaded += (_, _) => Pause();
        DataContextChanged += (_, _) => Bind();
        _bar.SizeChanged += (_, _) => Paint();
    }

    private static TextBlock Cell() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        MaxLines = 1,
    };

    /// <summary>The chosen style; a cell whose Tag names one (Settings' previews) keeps it.</summary>
    private string RowStyle => Tag as string ?? ViewSettings.Current.ProgressStyle;

    private void Resume()
    {
        if (_live) return;
        _live = true;
        Appearance.RowsChanged += Paint;
        ThemeColors.Changed += Paint;
        ViewSettings.Current.PropertyChanged += OnViewChanged;
        Bind();
    }

    private void Pause()
    {
        _live = false;
        Appearance.RowsChanged -= Paint;
        ThemeColors.Changed -= Paint;
        ViewSettings.Current.PropertyChanged -= OnViewChanged;
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = null;
        StopMotion();
    }

    private void Bind()
    {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = _live ? DataContext as DownloadItem : null;
        if (_item is not null) _item.PropertyChanged += OnItemChanged;
        // A recycled row shows another download: whatever moved stops first.
        StopMotion();
        Paint();
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewSettings.RowHeight)) Paint();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update sends every property in one go; FillScale comes last of the ones drawn here.
        if (e.PropertyName is nameof(DownloadItem.FillScale) or nameof(DownloadItem.StatusBrush)) Paint();
    }

    // ----- colours and sizes -----

    private static double Strength => ViewSettings.Current.FillStrength switch { "subtle" => 0.6, "vivid" => 1.5, _ => 1.0 };

    /// <summary>The state's colour, or the file type's while it runs and rows are coloured by type.</summary>
    private static Color ColorOf(DownloadItem item) => item.State switch
    {
        "paused" => ThemeColors.Caution,
        "failed" => ThemeColors.Critical,
        "completed" or "finishing" => ThemeColors.Success,
        "queued" => ThemeColors.SecondaryText,
        _ when ViewSettings.Current.ColorBy == "type" => ThemeColors.ForCategory(item.Category),
        _ => ThemeColors.Accent,
    };

    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(a, 0, 1)), c.R, c.G, c.B);

    private static Color Ink => ThemeColors.IsDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);

    /// <summary>Movement is allowed: the Animation setting is on and Windows' animation effects are too.</summary>
    private static bool Motion => ViewSettings.Current.Animation != "off" && RowFill.WindowsAnimates;

    private static bool Lively => ViewSettings.Current.Animation == "lively";

    // ----- drawing -----

    private void Paint()
    {
        var style = RowStyle;
        var ring = style == "ring";
        var bar = style == "bar";
        var item = _item;
        _words.Visibility = !ring && !bar ? Visibility.Visible : Visibility.Collapsed;
        _ringRow.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        _barStack.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) { StopMotion(); return; }

        if (ring) PaintRing(item);
        else if (bar) PaintBar(item);
        else
        {
            StopMotion();
            PaintWords(item);
        }
    }

    private void PaintWords(DownloadItem item)
    {
        _glyph.Glyph = item.StateGlyph;
        _glyph.Visibility = item.StateVisible ? Visibility.Visible : Visibility.Collapsed;
        _glyph.Foreground = item.StatusBrush;
        _text.Text = item.ProgressText;
        _text.Foreground = item.StatusBrush;
    }

    /// <summary>How far along, 0 to 1, or null while the size is unknown and it is still running.</summary>
    private static double? Fraction(DownloadItem item)
    {
        if (item.IsCompleted || item.State == "finishing") return 1;
        if (item.TotalSize is > 0) return Math.Clamp(item.Progress / 100, 0, 1);
        return item.IsActive ? null : 0;
    }

    private void PaintRing(DownloadItem item)
    {
        // A small ring, the size of an icon; the words beside it carry the number.
        var size = ViewSettings.Current.Density switch { "compact" => 14.0, "comfortable" => 18.0, _ => 16.0 };
        var k = Strength;
        var thickness = size / 8 * (k < 1 ? 0.8 : k > 1 ? 1.25 : 1);
        var color = ColorOf(item);
        var fraction = Fraction(item);

        _ring.Width = _ring.Height = size;
        _track.StrokeThickness = _arc.StrokeThickness = thickness;
        _track.Stroke = new SolidColorBrush(Alpha(Ink, ThemeColors.IsDark ? 0.18 : 0.13));
        _arc.Stroke = new SolidColorBrush(Alpha(color, 0.45 + 0.55 * k));
        _ringText.Text = "";
        _ringGlyph.Visibility = Visibility.Collapsed;

        // Beside it: "62%", "Paused 62%", "12.3 MB" while the size is unknown, "Done", "Failed"...
        _ringLabel.Text = item.ProgressText;
        _ringLabel.Foreground = item.StatusBrush;
        _ringLabel.Visibility = _ringLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (fraction is null)
        {
            // Size unknown: a quarter arc goes round while it runs; it rests at the top without motion.
            _arc.Data = Arc(size, thickness, 0.28);
            StartSpin(size);
        }
        else
        {
            StopMotion();
            _arc.Data = fraction >= 0.999 ? Circle(size, thickness) : fraction <= 0 ? null : Arc(size, thickness, fraction.Value);
        }
    }

    private static Geometry Circle(double size, double thickness)
    {
        var r = (size - thickness) / 2;
        return new EllipseGeometry { Center = new Windows.Foundation.Point(size / 2, size / 2), RadiusX = r, RadiusY = r };
    }

    /// <summary>An arc from twelve o'clock, clockwise, <paramref name="fraction"/> of the way round.</summary>
    private static Geometry Arc(double size, double thickness, double fraction)
    {
        var r = (size - thickness) / 2;
        var c = size / 2;
        var angle = Math.Max(0.02, fraction) * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(c, c - r), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)),
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = angle > Math.PI,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void PaintBar(DownloadItem item)
    {
        var k = Strength;
        var color = ColorOf(item);
        var fraction = Fraction(item);
        var height = k < 1 ? 3.0 : k > 1 ? 6.0 : 4.0;
        // A bar while there is progress to show; the done, queued and failed-at-nothing rows say so in words.
        var hasBar = item.BarVisible || (item.IsFailed && item.Downloaded > 0 && item.TotalSize is > 0);

        _barText.Text = item.BarVisible ? item.ProgressText : (item.StateGlyph.Length > 0 ? item.StateText : item.ProgressText);
        _barText.Foreground = item.StatusBrush;
        // Over a bar the text is small, so the two fit the row; on its own it is the column's usual size.
        _barText.FontSize = hasBar ? 12 : 14;
        _bar.Visibility = hasBar ? Visibility.Visible : Visibility.Collapsed;
        _bar.Height = height;
        _bar.CornerRadius = _barFill.CornerRadius = new CornerRadius(height / 2);
        _bar.Background = new SolidColorBrush(Alpha(Ink, ThemeColors.IsDark ? 0.14 : 0.09));
        _barFill.Background = new SolidColorBrush(Alpha(color, 0.45 + 0.55 * k));
        if (!hasBar) { StopMotion(); return; }

        var width = _bar.ActualWidth;
        if (width <= 0) return;
        if (fraction is null)
        {
            // Size unknown: a short segment runs along the bar; it sits still at the start without motion.
            _barFill.Width = width * 0.3;
            StartSlide((float)width);
        }
        else
        {
            _barFill.Width = Math.Max(height, width * fraction.Value);
            if (item.State == "downloading" && item.TotalSize is > 0) StartGlint((float)width);
            else StopMotion();
        }
    }

    // ----- motion, on the compositor so it costs the UI thread nothing -----

    private void StartSpin(double size)
    {
        if (!Motion) { StopMotion(); return; }
        var visual = ElementCompositionPreview.GetElementVisual(_arc);
        visual.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        if (_spinning == "ring" + Lively) return;
        StopMotion();
        var spin = visual.Compositor.CreateScalarKeyFrameAnimation();
        spin.InsertKeyFrame(0, 0);
        spin.InsertKeyFrame(1, 360, visual.Compositor.CreateLinearEasingFunction());
        spin.Duration = TimeSpan.FromSeconds(Lively ? 0.9 : 1.5);
        spin.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), spin);
        _spinning = "ring" + Lively;
    }

    private void StartSlide(float width)
    {
        if (!Motion) { StopMotion(); return; }
        if (_spinning == "bar" + Lively && Math.Abs(_spinWidth - width) < 1) return;
        StopMotion();
        var visual = ElementCompositionPreview.GetElementVisual(_barFill);
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(-width * 0.3f, 0, 0));
        slide.InsertKeyFrame(1, new Vector3(width, 0, 0), visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        slide.Duration = TimeSpan.FromSeconds(Lively ? 1.1 : 1.8);
        slide.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation", slide);
        _spinning = "bar" + Lively;
        _spinWidth = width;
    }

    /// <summary>A soft glint runs along the filled part every few seconds while bytes arrive.</summary>
    private void StartGlint(float width)
    {
        if (!Motion) { StopMotion(); return; }
        if (_spinning == "glint" + Lively && Math.Abs(_spinWidth - width) < 1) return;
        StopMotion();
        var white = Color.FromArgb(150, 255, 255, 255);
        _barGlint.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0 },
                new GradientStop { Color = white, Offset = 0.5 },
                new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
            },
        };
        _barGlint.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(_barGlint);
        var travel = Lively ? 1.0 : 1.6;
        var rest = Lively ? 0.6 : 1.6;
        var glide = visual.Compositor.CreateVector3KeyFrameAnimation();
        glide.InsertKeyFrame(0, new Vector3(-24, 0, 0));
        glide.InsertKeyFrame((float)(travel / (travel + rest)), new Vector3(width, 0, 0), visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.7f, 1f)));
        glide.InsertKeyFrame(1, new Vector3(width, 0, 0));
        glide.Duration = TimeSpan.FromSeconds(travel + rest);
        glide.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation", glide);
        _spinning = "glint" + Lively;
        _spinWidth = width;
    }

    private void StopMotion()
    {
        if (_spinning.Length == 0) return;
        var arc = ElementCompositionPreview.GetElementVisual(_arc);
        arc.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        arc.RotationAngleInDegrees = 0;
        var fill = ElementCompositionPreview.GetElementVisual(_barFill);
        fill.StopAnimation("Translation");
        fill.Properties.InsertVector3("Translation", Vector3.Zero);
        var glint = ElementCompositionPreview.GetElementVisual(_barGlint);
        glint.StopAnimation("Translation");
        _barGlint.Visibility = Visibility.Collapsed;
        _spinning = "";
    }
}
