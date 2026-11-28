// Lanczos-3 downscaling for render-icon.ps1 (loaded with Add-Type): scales a PNG master down in
// premultiplied alpha, so the transparent surround cannot darken the edge, with an optional light
// unsharp mask for the small sizes.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class OdmLanczos
{
    static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= 3) return 0;
        double p = Math.PI * x;
        return 3 * Math.Sin(p) * Math.Sin(p / 3) / (p * p);
    }

    // One separable pass: horizontal (width sw -> d) or vertical (height sh -> d). Pixels are RGBA doubles.
    static double[] Pass(double[] src, int sw, int sh, int d, bool horizontal)
    {
        int n = horizontal ? sw : sh, ow = horizontal ? d : sw, oh = horizontal ? sh : d, lines = horizontal ? sh : sw;
        double scale = (double)n / d, support = 3 * scale;
        var dst = new double[ow * oh * 4];
        for (int o = 0; o < d; o++)
        {
            double c = (o + 0.5) * scale;
            int lo = (int)Math.Floor(c - support), hi = (int)Math.Ceiling(c + support);
            var w = new double[hi - lo + 1];
            double sum = 0;
            for (int i = lo; i <= hi; i++) { w[i - lo] = Kernel((i + 0.5 - c) / scale); sum += w[i - lo]; }
            for (int l = 0; l < lines; l++)
                for (int ch = 0; ch < 4; ch++)
                {
                    double acc = 0;
                    for (int i = lo; i <= hi; i++)
                    {
                        int s = Math.Min(n - 1, Math.Max(0, i));
                        acc += w[i - lo] * (horizontal ? src[(l * sw + s) * 4 + ch] : src[(s * sw + l) * 4 + ch]);
                    }
                    if (horizontal) dst[(l * ow + o) * 4 + ch] = acc / sum; else dst[(o * ow + l) * 4 + ch] = acc / sum;
                }
        }
        return dst;
    }

    public static void Resize(string from, int size, double sharpen, string to)
    {
        double[] px; int w, h;
        using (var src = new Bitmap(from))
        {
            w = src.Width; h = src.Height; px = new double[w * h * 4];
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var b = new byte[w * h * 4];
            Marshal.Copy(data.Scan0, b, 0, b.Length);
            src.UnlockBits(data);
            for (int i = 0; i < w * h; i++)
            {
                double a = b[i * 4 + 3] / 255.0;
                px[i * 4] = b[i * 4] * a; px[i * 4 + 1] = b[i * 4 + 1] * a; px[i * 4 + 2] = b[i * 4 + 2] * a; px[i * 4 + 3] = b[i * 4 + 3];
            }
        }
        var r = Pass(Pass(px, w, h, size, true), size, h, size, false);
        if (sharpen > 0)   // unsharp mask: the image plus `sharpen` times its difference from a 3x3 blur
        {
            var s = (double[])r.Clone();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double blur = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                blur += r[(Math.Min(size - 1, Math.Max(0, y + dy)) * size + Math.Min(size - 1, Math.Max(0, x + dx))) * 4 + ch];
                        int i = (y * size + x) * 4 + ch;
                        s[i] = r[i] + sharpen * (r[i] - blur / 9);
                    }
            r = s;
        }
        var outBytes = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            double a = Math.Min(255, Math.Max(0, r[i * 4 + 3]));
            for (int ch = 0; ch < 3; ch++)
            {
                double v = a > 0.5 ? r[i * 4 + ch] * 255 / a : 0;
                outBytes[i * 4 + ch] = (byte)Math.Round(Math.Min(255, Math.Max(0, v)));
            }
            outBytes[i * 4 + 3] = (byte)Math.Round(a);
        }
        using (var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            var data = dst.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(outBytes, 0, data.Scan0, outBytes.Length);
            dst.UnlockBits(data);
            dst.Save(to, ImageFormat.Png);
        }
    }
}
