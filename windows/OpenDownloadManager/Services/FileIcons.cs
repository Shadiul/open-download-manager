using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace OpenDownloadManager.Services;

/// <summary>
/// The icons Explorer shows: a thumbnail for finished files (a frame for videos, the picture for
/// images) and the file type's own icon for everything else, cached per extension.
/// </summary>
public static class FileIcons
{
    private static readonly Dictionary<string, Task<ImageSource?>> TypeIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A thumbnail of the finished file, or its type's icon.</summary>
    public static async Task<ImageSource?> ForAsync(string fileName, string? finishedPath, uint size = 48)
    {
        if (!string.IsNullOrEmpty(finishedPath) && File.Exists(finishedPath))
        {
            var thumb = await ThumbnailAsync(finishedPath, ThumbnailMode.SingleItem, size);
            if (thumb is not null) return thumb;
        }
        return await TypeIconAsync(Path.GetExtension(fileName), size);
    }

    public static Task<ImageSource?> TypeIconAsync(string extension, uint size = 48)
    {
        var ext = string.IsNullOrEmpty(extension) ? ".bin" : extension;
        lock (TypeIcons)
        {
            if (!TypeIcons.TryGetValue(ext, out var task))
            {
                task = LoadTypeIconAsync(ext, size);
                TypeIcons[ext] = task;
            }
            return task;
        }
    }

    /// <summary>
    /// The type's icon straight from the shell (SHGetFileInfo), which needs no real file and works
    /// for every extension, unlike thumbnails of an empty sample file.
    /// </summary>
    private static async Task<ImageSource?> LoadTypeIconAsync(string ext, uint size)
    {
        try
        {
            byte[]? png = ShellIconPng(ext);
            if (png is null) return null;
            var image = new BitmapImage();
            using var stream = new MemoryStream(png).AsRandomAccessStream();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ShellIconPng(string ext)
    {
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        var info = new SHFILEINFO();
        var ok = SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static async Task<ImageSource?> ThumbnailAsync(string path, ThumbnailMode mode, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(mode, size, ThumbnailOptions.UseCurrentScale);
            if (thumb is null || thumb.Size == 0) return null;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumb);
            return image;
        }
        catch
        {
            return null;
        }
    }
}
