using System.Runtime.InteropServices;

namespace OpenDownloadManager.Services;

public static class AppPaths
{
    public const string AppName = "Open Download Manager";

    /// <summary>%LOCALAPPDATA%\OpenDownloadManager: the state database and app settings.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDownloadManager");

    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static string ToolsDir { get; } = Path.Combine(AppDir, "tools");

    public static string HostExe { get; } = Path.Combine(AppDir, "odm-host.exe");

    public static string AppExe { get; } = Path.Combine(AppDir, "OpenDownloadManager.exe");

    public static string IconPath { get; } = Path.Combine(AppDir, "Assets", "app.ico");

    /// <summary>The user's Downloads folder, wherever they moved it.</summary>
    public static string DownloadsDir { get; } = KnownDownloads();

    private static string KnownDownloads()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var path) == 0 && path != IntPtr.Zero)
        {
            try { return Marshal.PtrToStringUni(path) ?? Fallback(); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Fallback();

        static string Fallback() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
