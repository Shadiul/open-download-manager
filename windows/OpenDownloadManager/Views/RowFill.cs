using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.UI;

namespace OpenDownloadManager.Views;

/// <summary>
/// The row as a progress bar, drawn as a dot matrix: lit dots in the state's colour (accent while
/// running, amber when paused, red when failed) up to the progress, and faint unlit dots after it.
/// While bytes arrive, a soft band of brighter dots sweeps across the lit part, like a scanning LED
/// panel.
///
/// The row template holds a Grid with two children: the unlit dots, and a Grid of lit dots revealed
/// by a composition clip (so the dots never stretch) holding the sweep's two layers.
/// <see cref="Attach"/> drives them.
/// </summary>
public static class RowFill
{
    /// <summary>Dot spacing and size, in view pixels.</summary>
    private const double Pitch = 2, Dot = 1;

    /// <summary>The dot images are this wide; rows are clipped to their own width.</summary>
    private const int Width = 3840;

    /// <summary>The sweep: a wide faint band with a narrower brighter core, in view pixels.</summary>
    private const float Band = 90, Core = 36;

    private static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(450);
    private static readonly ConditionalWeakTable<Grid, State> States = [];
    private static readonly Dictionary<(Color, int, bool, double), ImageBrush> Images = [];

    public static void Attach(Grid root)
    {
        try
        {
            if (!States.TryGetValue(root, out var state)) States.Add(root, state = new State(root));
            state.Resume();
        }
        catch (Exception e)
        {
            // A row without its fill still works; the log says why.
            Log.Error("RowFill.Attach", e);
        }
    }

    /// <summary>
    /// A dot-matrix image for one row height, drawn at the screen's real pixel size so the dots stay
    /// crisp. Lit dots vary a little in brightness, like an LED panel; even ones (the unlit dots and
    /// the sweep) all take the colour's own alpha.
    /// </summary>
    private static ImageBrush DotImage(Color color, int height, bool even, double scale)
    {
        var key = (color, height, even, scale);
        if (Images.TryGetValue(key, out var cached)) return cached;
        int w = (int)Math.Ceiling(Width * scale), h = Math.Max(1, (int)Math.Ceiling(height * scale));
        int pitch = Math.Max(2, (int)Math.Round(Pitch * scale)), dot = Math.Max(1, (int)Math.Round(Dot * scale));
        var rows = Math.Max(1, (h - dot) / pitch + 1);
        var top = (h - ((rows - 1) * pitch + dot)) / 2;
        var pixels = new byte[w * h * 4];
        for (var cx = 0; cx * pitch + dot <= w; cx++)
        {
            for (var r = 0; r < rows; r++)
            {
                double a;
                if (even) a = color.A;
                else
                {
                    // Steady per dot: a hash of its place, softened so the band reads as one.
                    var n = (uint)(cx * 73856093) ^ (uint)(r * 19349663);
                    n ^= n >> 13; n *= 0x5bd1e995; n ^= n >> 15;
                    a = Math.Min(255, (40 + (n % 1000) / 1000.0 * 60) * color.A / 170.0);
                }
                var alpha = (byte)a;
                // Premultiplied BGRA.
                byte b = (byte)(color.B * alpha / 255), g = (byte)(color.G * alpha / 255), rr = (byte)(color.R * alpha / 255);
                for (var y = top + r * pitch; y < top + r * pitch + dot && y < h; y++)
                {
                    for (var x = cx * pitch; x < cx * pitch + dot; x++)
                    {
                        var i = (y * w + x) * 4;
                        pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = rr; pixels[i + 3] = alpha;
                    }
                }
            }
        }
        var bitmap = new WriteableBitmap(w, h);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        // Fill the row's height exactly: the image then shows at one image pixel per screen pixel.
        return Images[key] = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Center,
        };
    }

    /// <summary>Diagonal stripes, two tones of one colour, drawn at the screen's real pixel size.</summary>
    private static ImageBrush StripeImage(Color color, int height, double scale, double strong, double weak)
    {
        var key = (Color.FromArgb((byte)(strong * 255), color.R, color.G, color.B), height, true, -scale - weak);
        if (Images.TryGetValue(key, out var cached)) return cached;
        int w = (int)Math.Ceiling(Width * scale), h = Math.Max(1, (int)Math.Ceiling(height * scale));
        var period = Math.Max(4, (int)Math.Round(10 * scale));
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var alpha = (byte)Math.Round(255 * Math.Clamp(((x + y) % period) < period / 2 ? strong : weak, 0, 1));
                var i = (y * w + x) * 4;
                pixels[i] = (byte)(color.B * alpha / 255); pixels[i + 1] = (byte)(color.G * alpha / 255);
                pixels[i + 2] = (byte)(color.R * alpha / 255); pixels[i + 3] = alpha;
            }
        }
        var bitmap = new WriteableBitmap(w, h);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        return Images[key] = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Center };
    }

    private static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(a, 0, 1)), c.R, c.G, c.B);

    private sealed class State
    {
        private readonly Grid _root;
        private readonly Border _unlit, _wide, _core;
        private readonly Grid _lit;
        private readonly Compositor _compositor;
        private readonly InsetClip _clip, _wideClip, _coreClip;
        private readonly CompositionPropertySet _sweep, _pass2;
        private readonly CompositionEasingFunction _ease;
        private DownloadItem? _item;
        private bool _placed, _live, _sweeping;

        public State(Grid root)
        {
            _root = root;
            _unlit = (Border)root.Children[0];
            _lit = (Grid)root.Children[1];
            _wide = (Border)_lit.Children[0];
            _core = (Border)_lit.Children[1];
            var visual = ElementCompositionPreview.GetElementVisual(_lit);
            _compositor = visual.Compositor;
            _clip = _compositor.CreateInsetClip();
            visual.Clip = _clip;
            _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

            // The sweep's position X and the row's width W drive both layers' clips, so the bright
            // dots stay exactly over the lit ones while the band moves.
            _sweep = _compositor.CreatePropertySet();
            _sweep.InsertScalar("X", -Band);
            _sweep.InsertScalar("W", 0);
            // P runs 0 to 1 over a pass; E carries the band past the end while finishing.
            _pass2 = _compositor.CreatePropertySet();
            _pass2.InsertScalar("P", 0);
            _pass2.InsertScalar("E", 0);
            _wideClip = Band2Clip(_wide, Band);
            _coreClip = Band2Clip(_core, Core);
            // The other styles move a soft-edged band instead (see UseSoftBand).
            ElementCompositionPreview.SetIsTranslationEnabled(_wide, true);
            ElementCompositionPreview.SetIsTranslationEnabled(_core, true);
            // The band heads for the progress front as it is at every frame (the lit part's clip),
            // not as it was when the pass began, so it always arrives at the front.
            var follow = _compositor.CreateExpressionAnimation($"Lerp({-Band / 2}, s.W - c.RightInset + p.E, p.P)");
            follow.SetReferenceParameter("s", _sweep);
            follow.SetReferenceParameter("c", _clip);
            follow.SetReferenceParameter("p", _pass2);
            _sweep.StartAnimation("X", follow);

            root.DataContextChanged += (_, _) => Bind();
            root.SizeChanged += (_, _) => { Paint(); Place(animate: false); Sweep(); };
            root.Unloaded += (_, _) => Pause();
        }

        /// <summary>Clips a full-width layer to a band of this width centred on the sweep's X.</summary>
        private InsetClip Band2Clip(UIElement layer, float width)
        {
            var clip = _compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(layer).Clip = clip;
            var left = _compositor.CreateExpressionAnimation($"Max(0, s.X - {width / 2})");
            left.SetReferenceParameter("s", _sweep);
            var right = _compositor.CreateExpressionAnimation($"Max(0, s.W - s.X - {width / 2})");
            right.SetReferenceParameter("s", _sweep);
            clip.StartAnimation(nameof(InsetClip.LeftInset), left);
            clip.StartAnimation(nameof(InsetClip.RightInset), right);
            return clip;
        }

        public void Resume()
        {
            if (_live) return;
            _live = true;
            ThemeColors.Changed += OnTheme;
            Appearance.RowsChanged += OnStyle;
            Bind();
        }

        private void Pause()
        {
            _live = false;
            ThemeColors.Changed -= OnTheme;
            Appearance.RowsChanged -= OnStyle;
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = null;
            StopSweep();
        }

        private void OnTheme() => Paint();

        /// <summary>The row style, animation or colouring changed in Settings.</summary>
        private void OnStyle()
        {
            Paint();
            Place(animate: false);
            StopSweep();
            Sweep();
        }

        private void Bind()
        {
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = _live ? _root.DataContext as DownloadItem : null;
            if (_item is not null) _item.PropertyChanged += OnItemChanged;
            _placed = false;
            Paint();
            Place(animate: false);
            // A recycled row shows another download: its band starts over.
            StopSweep();
            Sweep();
        }

        private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadItem.FillScale)) Place(animate: true);
            else if (e.PropertyName == nameof(DownloadItem.FillBrush)) { Paint(); Place(animate: false); Sweep(); }
            // Queued or connecting to downloading keeps the same colour, but the sweep must start.
            else if (e.PropertyName == nameof(DownloadItem.State)) { Paint(); Sweep(); }
        }

        private Color StateColor => _item?.State switch
        {
            "paused" => ThemeColors.Caution,
            "failed" => ThemeColors.Critical,
            // Every byte is in, the file is being put together: the whole row in the success colour.
            "finishing" => ThemeColors.Success,
            _ when ViewSettings.Current.ColorBy == "type" && _item is not null => ThemeColors.ForCategory(_item.Category),
            _ => ThemeColors.Accent,
        };

        /// <summary>The chosen row style; a row whose Tag names a style (Settings' style tiles) keeps that one.</summary>
        private string Style => _root.Tag as string ?? ViewSettings.Current.ProgressStyle;

        private static double Strength => ViewSettings.Current.FillStrength switch { "subtle" => 0.6, "vivid" => 1.5, _ => 1.0 };

        /// <summary>
        /// Draws the row in the chosen style: the lit part up to the progress, a faint track after it,
        /// and the two layers of the sweep, all in the state's (or file type's) colour.
        /// </summary>
        private void Paint()
        {
            var height = (int)Math.Round(_root.ActualHeight);
            if (_item is null || height <= 0 || _root.XamlRoot is null) return;
            var scale = _root.XamlRoot.RasterizationScale;
            var color = StateColor;
            var k = Strength;
            var dark = ThemeColors.IsDark;
            var ink = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
            var style = Style;

            // A thin line along the bottom of the row; every other style fills the row's height.
            var underline = style == "underline";
            foreach (var layer in new FrameworkElement[] { _unlit, _lit })
            {
                layer.Height = underline ? 3 : double.NaN;
                layer.VerticalAlignment = underline ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
                layer.Visibility = style == "none" ? Visibility.Collapsed : Visibility.Visible;
            }

            switch (style)
            {
                case "solid":
                    _lit.Background = new SolidColorBrush(Alpha(color, (dark ? 0.24 : 0.17) * k));
                    _unlit.Background = new SolidColorBrush(Alpha(ink, 0.035));
                    UseSoftBand(true, Alpha(color, 0.16 * k), Alpha(color, 0.26 * k));
                    break;
                case "glass":
                    _lit.Background = Vertical(Alpha(color, (dark ? 0.34 : 0.26) * k), Alpha(color, 0.08 * k));
                    _unlit.Background = Vertical(Alpha(ink, 0.04), Alpha(ink, 0.01));
                    UseSoftBand(true, Alpha(color, 0.18 * k), Alpha(color, 0.30 * k));
                    break;
                case "stripes":
                    _lit.Background = StripeImage(color, height, scale, (dark ? 0.30 : 0.22) * k, (dark ? 0.14 : 0.09) * k);
                    _unlit.Background = new SolidColorBrush(Alpha(ink, 0.03));
                    UseSoftBand(true, Alpha(color, 0.14 * k), Alpha(color, 0.24 * k));
                    break;
                case "underline":
                    _lit.Background = new SolidColorBrush(Alpha(color, Math.Min(1, 0.85 * k)));
                    _unlit.Background = new SolidColorBrush(Alpha(ink, 0.10));
                    // A glint running along the line.
                    UseSoftBand(true, Color.FromArgb(120, 255, 255, 255), Color.FromArgb(200, 255, 255, 255));
                    break;
                case "none":
                    break;
                default:
                    _lit.Background = DotImage(Alpha(color, k / 1.5), height, even: false, scale);
                    _unlit.Background = DotImage(dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0), height, even: true, scale);
                    // The sweep: the same dots, evenly brighter; the core brighter still.
                    UseSoftBand(false, default, default);
                    _wide.Background = DotImage(Alpha(color, 70 / 255.0 * k), height, even: true, scale);
                    _core.Background = DotImage(Alpha(color, 120 / 255.0 * k), height, even: true, scale);
                    break;
            }
        }

        /// <summary>
        /// Dots: the band is the same dots, brighter, cut out by a moving clip, so they line up with
        /// the lit ones. Other styles: a narrow band with soft edges slides along instead.
        /// </summary>
        private void UseSoftBand(bool soft, Color wide, Color core)
        {
            foreach (var (layer, clip, width, color) in new[] { (_wide, _wideClip, Band * 1.6f, wide), (_core, _coreClip, Core * 1.6f, core) })
            {
                var visual = ElementCompositionPreview.GetElementVisual(layer);
                if (soft)
                {
                    layer.Width = width;
                    layer.HorizontalAlignment = HorizontalAlignment.Left;
                    layer.Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0.5),
                        EndPoint = new Windows.Foundation.Point(1, 0.5),
                        GradientStops =
                        {
                            new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 0 },
                            new GradientStop { Color = color, Offset = 0.5 },
                            new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 },
                        },
                    };
                    visual.Clip = null;
                    var move = _compositor.CreateExpressionAnimation($"Vector3(s.X - {width / 2}, 0, 0)");
                    move.SetReferenceParameter("s", _sweep);
                    visual.StartAnimation("Translation", move);
                }
                else
                {
                    layer.Width = double.NaN;
                    layer.HorizontalAlignment = HorizontalAlignment.Stretch;
                    visual.StopAnimation("Translation");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    visual.Clip = clip;
                }
            }
        }

        private static LinearGradientBrush Vertical(Color top, Color bottom) => new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { new GradientStop { Color = top, Offset = 0 }, new GradientStop { Color = bottom, Offset = 1 } },
        };

        private void Place(bool animate)
        {
            var width = _root.ActualWidth;
            if (_item is null || width <= 0) return;
            var f = Math.Clamp(_item.FillScale.X, 0, 1);
            var inset = (float)(width * (1 - f));
            if (animate && _placed)
            {
                var clipMove = _compositor.CreateScalarKeyFrameAnimation();
                clipMove.InsertKeyFrame(1, inset, _ease);
                clipMove.Duration = Glide;
                _clip.StartAnimation(nameof(InsetClip.RightInset), clipMove);
            }
            else
            {
                _clip.StopAnimation(nameof(InsetClip.RightInset));
                _clip.RightInset = inset;
            }
            _placed = true;
        }

        /// <summary>How fast the band travels, in view pixels per second.</summary>
        private static double SweepSpeed => ViewSettings.Current.Animation == "lively" ? 200 : 110;

        /// <summary>
        /// While the download runs, the band travels from the start of the row to the progress front
        /// (following the front as it moves), rests there while it fades, and goes again.
        /// Progress updates arrive several times a second; they must not restart a pass that is under
        /// way, or the band never gets far from the start. Only starting, stopping or a change between
        /// downloading and finishing starts a new pass.
        /// </summary>
        private void Sweep()
        {
            var width = (float)_root.ActualWidth;
            var running = _item?.State is "downloading" or "finishing" && width > 0 && _live
                && ViewSettings.Current.Animation != "off" && Style != "none";
            if (!running) { StopSweep(); return; }
            _sweep.InsertScalar("W", width);
            var finishing = _item!.State == "finishing";
            if (_sweeping && finishing == _sweepFinishing) return;
            _sweepFinishing = finishing;
            _wide.Visibility = _core.Visibility = Visibility.Visible;
            _sweeping = true;
            _pass++;
            StartPass(_pass);
        }

        private int _pass;
        private bool _sweepFinishing;

        private void StartPass(int pass)
        {
            if (!_sweeping || pass != _pass || _item is null) return;
            var width = (float)_root.ActualWidth;
            var front = (float)(width * Math.Clamp(_item.FillScale.X, 0, 1));
            // Finishing: the band runs the whole row and off its end, a little quicker, without resting.
            var finishing = _item.State == "finishing";
            _pass2.InsertScalar("E", finishing ? Band / 2 : 0);
            var distance = (finishing ? width + Band : front + Band / 2);
            var moving = Math.Clamp(distance / (finishing ? SweepSpeed * 1.6 : SweepSpeed), 1.6, 6.0);
            // The band rests at the front while it fades, then the next pass starts from the left.
            var rest = finishing ? 0.1 : ViewSettings.Current.Animation == "lively" ? 0.6 : 1.4;
            var arrive = (float)(moving / (moving + rest));
            var travel = _compositor.CreateScalarKeyFrameAnimation();
            travel.InsertKeyFrame(0f, 0f);
            travel.InsertKeyFrame(arrive, 1f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.7f, 1f)));
            travel.InsertKeyFrame(1f, 1f);
            travel.Duration = TimeSpan.FromSeconds(moving + rest);
            // Fades in at the start, stays bright all the way to the front, then fades out there.
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.12f * arrive, 1f);
            fade.InsertKeyFrame(arrive, 1f);
            fade.InsertKeyFrame(finishing ? 1f : Math.Min(1f, arrive + (float)(0.7 / (moving + rest))), 0f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = travel.Duration;
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _pass2.StartAnimation("P", travel);
            ElementCompositionPreview.GetElementVisual(_wide).StartAnimation("Opacity", fade);
            ElementCompositionPreview.GetElementVisual(_core).StartAnimation("Opacity", fade);
            batch.End();
            batch.Completed += (_, _) => _root.DispatcherQueue.TryEnqueue(() => StartPass(pass));
        }

        private void StopSweep()
        {
            if (!_sweeping && _wide.Visibility == Visibility.Collapsed) return;
            _pass++;
            _pass2.StopAnimation("P");
            _pass2.InsertScalar("P", 0);
            _wide.Visibility = _core.Visibility = Visibility.Collapsed;
            _sweeping = false;
        }
    }
}
