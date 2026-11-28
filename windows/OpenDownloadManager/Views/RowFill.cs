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
                    a = 40 + (n % 1000) / 1000.0 * 60;
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

    private sealed class State
    {
        private readonly Grid _root;
        private readonly Border _unlit, _wide, _core;
        private readonly Grid _lit;
        private readonly Compositor _compositor;
        private readonly InsetClip _clip, _wideClip, _coreClip;
        private readonly CompositionPropertySet _sweep;
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
            _wideClip = Band2Clip(_wide, Band);
            _coreClip = Band2Clip(_core, Core);

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
            Bind();
        }

        private void Pause()
        {
            _live = false;
            ThemeColors.Changed -= OnTheme;
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = null;
            StopSweep();
        }

        private void OnTheme() => Paint();

        private void Bind()
        {
            if (_item is not null) _item.PropertyChanged -= OnItemChanged;
            _item = _live ? _root.DataContext as DownloadItem : null;
            if (_item is not null) _item.PropertyChanged += OnItemChanged;
            _placed = false;
            Paint();
            Place(animate: false);
            Sweep();
        }

        private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadItem.FillScale)) Place(animate: true);
            else if (e.PropertyName == nameof(DownloadItem.FillBrush)) { Paint(); Place(animate: false); Sweep(); }
        }

        private Color StateColor => _item?.State switch
        {
            "paused" => ThemeColors.Caution,
            "failed" => ThemeColors.Critical,
            // Every byte is in, the file is being put together: the whole row in the success colour.
            "finishing" => ThemeColors.Success,
            _ => ThemeColors.Accent,
        };

        private void Paint()
        {
            var height = (int)Math.Round(_root.ActualHeight);
            if (_item is null || height <= 0 || _root.XamlRoot is null) return;
            var scale = _root.XamlRoot.RasterizationScale;
            var color = StateColor;
            _lit.Background = DotImage(color, height, even: false, scale);
            var faint = ThemeColors.IsDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);
            _unlit.Background = DotImage(faint, height, even: true, scale);
            // The sweep: the same dots, evenly brighter; the core brighter still.
            _wide.Background = DotImage(Color.FromArgb(70, color.R, color.G, color.B), height, even: true, scale);
            _core.Background = DotImage(Color.FromArgb(120, color.R, color.G, color.B), height, even: true, scale);
        }

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
        private const double SweepSpeed = 180;

        /// <summary>
        /// While the download runs, the band travels from the start of the row to the progress front,
        /// rests a moment, and goes again. Each pass is measured to the front as it is then, so the band
        /// always reaches the end of the lit part, at the same unhurried speed however long it is.
        /// </summary>
        private void Sweep()
        {
            var width = (float)_root.ActualWidth;
            var running = _item?.State is "downloading" or "finishing" && width > 0 && _live;
            if (!running) { StopSweep(); return; }
            _sweep.InsertScalar("W", width);
            _wide.Visibility = _core.Visibility = Visibility.Visible;
            _sweeping = true;
            _pass++;
            StartPass(_pass);
        }

        private int _pass;

        private void StartPass(int pass)
        {
            if (!_sweeping || pass != _pass || _item is null) return;
            var width = (float)_root.ActualWidth;
            var front = (float)(width * Math.Clamp(_item.FillScale.X, 0, 1));
            // From just before the row's start to the front, where the band ends half past it.
            var finishing = _item.State == "finishing";
            var from = -Band / 2;
            // Finishing: the band runs the whole row, quicker and without resting, until it is done.
            var to = finishing ? width + Band / 2 : front;
            var moving = Math.Max(0.6, (to - from) / (finishing ? SweepSpeed * 2.2 : SweepSpeed));
            var rest = finishing ? 0.05 : 0.7;
            var travel = _compositor.CreateScalarKeyFrameAnimation();
            travel.InsertKeyFrame(0f, from);
            travel.InsertKeyFrame((float)(moving / (moving + rest)), to, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
            travel.InsertKeyFrame(1f, to);
            travel.Duration = TimeSpan.FromSeconds(moving + rest);
            // The band fades in at the start and out at the front rather than popping.
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame((float)(0.15 * moving / (moving + rest)), 1f);
            fade.InsertKeyFrame((float)(0.85 * moving / (moving + rest)), 1f);
            fade.InsertKeyFrame((float)(moving / (moving + rest)), 0f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = travel.Duration;
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _sweep.StartAnimation("X", travel);
            ElementCompositionPreview.GetElementVisual(_wide).StartAnimation("Opacity", fade);
            ElementCompositionPreview.GetElementVisual(_core).StartAnimation("Opacity", fade);
            batch.End();
            batch.Completed += (_, _) => _root.DispatcherQueue.TryEnqueue(() => StartPass(pass));
        }

        private void StopSweep()
        {
            if (!_sweeping && _wide.Visibility == Visibility.Collapsed) return;
            _pass++;
            _sweep.StopAnimation("X");
            _sweep.InsertScalar("X", -Band);
            _wide.Visibility = _core.Visibility = Visibility.Collapsed;
            _sweeping = false;
        }
    }
}
