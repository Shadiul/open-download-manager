using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.UI;

namespace OpenDownloadManager.Views;

/// <summary>
/// The row as a progress bar, drawn as a dot matrix: lit dots in the state's colour (accent while
/// running, amber when paused, red when failed) up to the progress, faint unlit dots after it, and a
/// soft glow at the front that glides forward as bytes arrive.
///
/// The row template holds a Grid with three children: the unlit dots, the lit dots (revealed by a
/// composition clip, so the dots never stretch), and the glow. <see cref="Attach"/> drives them.
/// </summary>
public static class RowFill
{
    /// <summary>Dot spacing and size, in view pixels.</summary>
    private const double Pitch = 2, Dot = 1;

    /// <summary>The dot images are this wide; rows are clipped to their own width.</summary>
    private const int Width = 3840;

    private static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(450);
    private static readonly ConditionalWeakTable<Grid, State> States = [];
    private static readonly Dictionary<(Color, int, bool, double), ImageBrush> Images = [];

    public static void Attach(Grid root)
    {
        if (!States.TryGetValue(root, out var state)) States.Add(root, state = new State(root));
        state.Resume();
    }

    /// <summary>
    /// A dot-matrix image for one row height, drawn at the screen's real pixel size so the dots stay
    /// crisp. Lit dots vary a little in brightness, like an LED panel; unlit ones are even and faint.
    /// </summary>
    private static ImageBrush DotImage(Color color, int height, bool unlit, double scale)
    {
        var key = (color, height, unlit, scale);
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
                if (unlit) a = color.A;
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

    /// <summary>The glow at the front: a soft rise, a bright edge, and a short fade past it.</summary>
    private static Brush GlowBrush(Color c)
    {
        Color A(byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = A(0), Offset = 0 },
                new GradientStop { Color = A(30), Offset = 0.7 },
                new GradientStop { Color = A(80), Offset = 0.86 },
                new GradientStop { Color = A(255), Offset = EdgeAt },
                new GradientStop { Color = A(90), Offset = 0.9 },
                new GradientStop { Color = A(0), Offset = 1 },
            },
        };
    }

    /// <summary>Where the bright edge sits across the glow, as a fraction of its width.</summary>
    private const double EdgeAt = 0.885;

    private sealed class State
    {
        private readonly Grid _root;
        private readonly Border _unlit, _lit;
        private readonly Rectangle _glow;
        private readonly Compositor _compositor;
        private readonly InsetClip _clip;
        private readonly Visual _glowVisual;
        private readonly CompositionEasingFunction _ease;
        private DownloadItem? _item;
        private bool _placed, _live;

        public State(Grid root)
        {
            _root = root;
            _unlit = (Border)root.Children[0];
            _lit = (Border)root.Children[1];
            _glow = (Rectangle)root.Children[2];
            var visual = ElementCompositionPreview.GetElementVisual(_lit);
            _compositor = visual.Compositor;
            _clip = _compositor.CreateInsetClip();
            visual.Clip = _clip;
            ElementCompositionPreview.SetIsTranslationEnabled(_glow, true);
            _glowVisual = ElementCompositionPreview.GetElementVisual(_glow);
            _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
            root.DataContextChanged += (_, _) => Bind();
            root.SizeChanged += (_, _) => { Paint(); Place(animate: false); };
            root.Unloaded += (_, _) => Pause();
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
        }

        private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadItem.FillScale)) Place(animate: true);
            else if (e.PropertyName == nameof(DownloadItem.FillBrush)) { Paint(); Place(animate: false); }
        }

        private Color StateColor => _item?.State switch
        {
            "paused" => ThemeColors.Caution,
            "failed" => ThemeColors.Critical,
            _ => ThemeColors.Accent,
        };

        private void Paint()
        {
            var height = (int)Math.Round(_root.ActualHeight);
            if (_item is null || height <= 0 || _root.XamlRoot is null) return;
            var scale = _root.XamlRoot.RasterizationScale;
            var color = StateColor;
            _lit.Background = DotImage(color, height, unlit: false, scale);
            var faint = ThemeColors.IsDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);
            _unlit.Background = DotImage(faint, height, unlit: true, scale);
            _glow.Fill = GlowBrush(color);
        }

        private void Place(bool animate)
        {
            var width = _root.ActualWidth;
            if (_item is null || width <= 0) return;
            var f = Math.Clamp(_item.FillScale.X, 0, 1);
            var inset = (float)(width * (1 - f));
            var glowX = (float)(width * f - _glow.Width * EdgeAt);
            _glow.Visibility = _item.State == "downloading" && f is > 0 and < 0.999f ? Visibility.Visible : Visibility.Collapsed;
            if (animate && _placed)
            {
                var clipMove = _compositor.CreateScalarKeyFrameAnimation();
                clipMove.InsertKeyFrame(1, inset, _ease);
                clipMove.Duration = Glide;
                _clip.StartAnimation(nameof(InsetClip.RightInset), clipMove);
                var glowMove = _compositor.CreateVector3KeyFrameAnimation();
                glowMove.InsertKeyFrame(1, new Vector3(glowX, 0, 0), _ease);
                glowMove.Duration = Glide;
                _glowVisual.StartAnimation("Translation", glowMove);
            }
            else
            {
                _clip.StopAnimation(nameof(InsetClip.RightInset));
                _clip.RightInset = inset;
                _glowVisual.StopAnimation("Translation");
                _glowVisual.Properties.InsertVector3("Translation", new Vector3(glowX, 0, 0));
            }
            _placed = true;
        }
    }
}
