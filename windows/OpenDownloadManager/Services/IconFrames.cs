using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace OpenDownloadManager.Services;

/// <summary>
/// Draws the chosen app icon small and sharp: the .ico frame made for the size on screen (16 px at
/// 100%, 20 at 125%, 24 at 150%, 32 at 200%), the tile-less artwork render-icon.ps1 puts there,
/// instead of a scaled-down large picture.
/// </summary>
public static class IconFrames
{
    /// <summary>Shows the icon in an Image of the given size (in view pixels) at its current scale.</summary>
    public static async void Show(Image image, double size)
    {
        try
        {
            var px = (int)Math.Round(size * (image.XamlRoot?.RasterizationScale ?? 1));
            // Called again on every layout change of the window: only a new icon or scale redraws it.
            var key = $"{AppPaths.IconPath}|{px}";
            if (image.Tag as string == key) return;
            image.Tag = key;
            var (bytes, frame) = Frame(AppPaths.IconPath, px);
            if (bytes is null) return;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            if (frame != px) bitmap.DecodePixelWidth = px;   // no frame of that size: the next larger one, scaled once
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception e)
        {
            Log.Error("icon frame", e);
        }
    }

    /// <summary>The smallest frame at least px wide (else the largest), from an .ico with PNG frames.</summary>
    private static (byte[]? Bytes, int Size) Frame(string ico, int px)
    {
        var data = File.ReadAllBytes(ico);
        var count = BitConverter.ToUInt16(data, 4);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var size = data[6 + 16 * i] == 0 ? 256 : data[6 + 16 * i];
            var better = best < 0
                || (size >= px && (bestSize < px || size < bestSize))
                || (size < px && bestSize < px && size > bestSize);
            if (better) { best = i; bestSize = size; }
        }
        if (best < 0) return (null, 0);
        var length = (int)BitConverter.ToUInt32(data, 6 + 16 * best + 8);
        var offset = (int)BitConverter.ToUInt32(data, 6 + 16 * best + 12);
        return (data.AsSpan(offset, length).ToArray(), bestSize);
    }
}
