// Draws the Open Download Manager icon: a squircle tile with a blue-to-indigo gradient, a soft
// top highlight, and a white arrow dropping into a tray. Used by scripts/icon/render-icon.ps1.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class IconRenderer
{
    /// <param name="size">Output size in pixels.</param>
    /// <param name="plate">Fraction of the canvas the tile covers (macOS uses 824/1024).</param>
    /// <param name="shadow">Draw the soft shadow under the tile (macOS style).</param>
    /// <param name="bold">Thicker strokes and no tray detail, for 16 to 32 px.</param>
    public static Bitmap Render(int size, double plate, bool shadow, bool bold)
    {
        // Draw large and scale down: GDI+ antialiasing is best that way.
        int big = Math.Max(1024, size);
        var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = (float)(big * plate);
            float x0 = (big - s) / 2f, y0 = (big - s) / 2f;
            if (shadow) y0 -= big * 0.01f;
            var tile = new RectangleF(x0, y0, s, s);

            if (shadow)
            {
                // A soft drop shadow built from widening, fading layers.
                for (int i = 12; i >= 1; i--)
                {
                    float grow = i * big * 0.004f;
                    var r = new RectangleF(tile.X - grow, tile.Y - grow + big * 0.018f, tile.Width + 2 * grow, tile.Height + 2 * grow);
                    using (var p = Squircle(r))
                    using (var b = new SolidBrush(Color.FromArgb(6, 10, 20, 60)))
                        g.FillPath(b, p);
                }
            }

            using (var path = Squircle(tile))
            {
                using (var fill = new LinearGradientBrush(new PointF(tile.Left, tile.Top), new PointF(tile.Right, tile.Bottom),
                    Color.FromArgb(255, 72, 156, 255), Color.FromArgb(255, 66, 56, 214)))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { Color.FromArgb(255, 86, 178, 255), Color.FromArgb(255, 61, 116, 246), Color.FromArgb(255, 77, 55, 205) };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    fill.InterpolationColors = blend;
                    g.FillPath(fill, path);
                }

                // Top highlight: a gentle sheen on the upper half.
                var state = g.Save();
                g.SetClip(path);
                using (var sheen = new LinearGradientBrush(new PointF(0, tile.Top), new PointF(0, tile.Top + s * 0.55f),
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                    g.FillRectangle(sheen, tile.Left, tile.Top, s, s * 0.55f);
                // A darker floor under the tray gives depth.
                using (var floor = new LinearGradientBrush(new PointF(0, tile.Top + s * 0.6f), new PointF(0, tile.Bottom),
                    Color.FromArgb(0, 20, 10, 80), Color.FromArgb(60, 20, 10, 80)))
                    g.FillRectangle(floor, tile.Left, tile.Top + s * 0.6f, s, s * 0.4f + 1);
                g.Restore(state);

                // A thin inner edge, lighter at the top, like Fluent and macOS tiles.
                using (var edge = new Pen(new LinearGradientBrush(new PointF(0, tile.Top), new PointF(0, tile.Bottom),
                    Color.FromArgb(90, 255, 255, 255), Color.FromArgb(20, 255, 255, 255)), s * 0.006f))
                {
                    var inner = RectangleF.Inflate(tile, -s * 0.004f, -s * 0.004f);
                    using (var ip = Squircle(inner)) g.DrawPath(edge, ip);
                }
            }

            Func<double, float> U = f => (float)(s * f);
            Func<double, float> X = f => tile.Left + U(f);
            Func<double, float> Y = f => tile.Top + U(f);

            float shaft = U(bold ? 0.17 : 0.13);
            var white = Color.FromArgb(255, 255, 255, 255);

            // Arrow: rounded shaft and a rounded head, with a faint shadow below it.
            for (int pass = 0; pass < 2; pass++)
            {
                bool isShadow = pass == 0;
                float dy = isShadow ? U(0.02) : 0;
                var color = isShadow ? Color.FromArgb(55, 25, 20, 110) : white;
                using (var pen = new Pen(color, shaft) { StartCap = LineCap.Round, EndCap = LineCap.Flat, LineJoin = LineJoin.Round })
                    g.DrawLine(pen, X(0.5), Y(bold ? 0.2 : 0.18) + dy, X(0.5), Y(bold ? 0.5 : 0.47) + dy);
                using (var head = new GraphicsPath())
                {
                    float hw = U(bold ? 0.25 : 0.215), top = Y(bold ? 0.43 : 0.4) + dy, tip = Y(bold ? 0.7 : 0.64) + dy;
                    head.AddPolygon(new[] { new PointF(X(0.5) - hw, top), new PointF(X(0.5) + hw, top), new PointF(X(0.5), tip) });
                    using (var b = new SolidBrush(color)) g.FillPath(b, head);
                    using (var round = new Pen(color, U(0.06)) { LineJoin = LineJoin.Round }) g.DrawPath(round, head);
                }
            }

            if (!bold)
            {
                // The tray: a rounded U the arrow drops into.
                using (var tray = new GraphicsPath())
                {
                    float l = X(0.24), r = X(0.76), top = Y(0.62), bottom = Y(0.8), rad = U(0.07);
                    tray.AddLine(l, top, l, bottom - rad);
                    tray.AddArc(l, bottom - 2 * rad, 2 * rad, 2 * rad, 180, -90);
                    tray.AddLine(l + rad, bottom, r - rad, bottom);
                    tray.AddArc(r - 2 * rad, bottom - 2 * rad, 2 * rad, 2 * rad, 90, -90);
                    tray.AddLine(r, bottom - rad, r, top);
                    using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), U(0.075)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawPath(pen, tray);
                }
            }
        }
        if (size == big) return canvas;
        var output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(canvas, 0, 0, size, size);
        }
        canvas.Dispose();
        return output;
    }

    /// <summary>A superellipse (n = 5), the continuous-corner shape of macOS and Fluent tiles.</summary>
    static GraphicsPath Squircle(RectangleF r)
    {
        const double n = 5;
        var pts = new PointF[360];
        double a = r.Width / 2, b = r.Height / 2, cx = r.X + a, cy = r.Y + b;
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length, c = Math.Cos(t), sn = Math.Sin(t);
            double x = Math.Pow(Math.Abs(c), 2 / n) * a * Math.Sign(c);
            double y = Math.Pow(Math.Abs(sn), 2 / n) * b * Math.Sign(sn);
            pts[i] = new PointF((float)(cx + x), (float)(cy + y));
        }
        var p = new GraphicsPath();
        p.AddPolygon(pts);
        return p;
    }
}
