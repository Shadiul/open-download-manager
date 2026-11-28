using System.Runtime.InteropServices;

namespace OpenDownloadManager.Services;

/// <summary>Errors worth keeping for a bug report, in %LOCALAPPDATA%\OpenDownloadManager\app.log.</summary>
public static class Log
{
    public static void Error(string where, Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var path = Path.Combine(AppPaths.DataDir, "app.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {e}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

public static class AppPaths
{
    /// <summary>
    /// For development: ODM_PROFILE=name runs a separate copy with its own data, its own browser
    /// endpoint, and no browser or Windows registration, next to the everyday one.
    /// </summary>
    public static string? Profile { get; } =
        Environment.GetEnvironmentVariable("ODM_PROFILE") is { Length: > 0 } p && p.All(char.IsLetterOrDigit) ? p : null;

    /// <summary>
    /// A Debug build or a profile copy: it starts on the Smoke icon and shows "(Dev)" or "(profile)" in
    /// its titles and tray tooltip, so it can't be mistaken for the installed app next to it.
    /// </summary>
#if DEBUG
    public static bool IsDev => true;
#else
    public static bool IsDev => Profile is not null;
#endif

    /// <summary>The app's name, with " (Dev)" or " (profile)" after it in a development copy.</summary>
    public static string AppName { get; } = "Open Download Manager" + (IsDev ? $" ({Profile ?? "Dev"})" : "");

    /// <summary>%LOCALAPPDATA%\OpenDownloadManager: the state database and app settings.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Profile is null ? "OpenDownloadManager" : $"OpenDownloadManager-{Profile}");

    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static string ToolsDir { get; } = Path.Combine(AppDir, "tools");

    public static string HostExe { get; } = Path.Combine(AppDir, "odm-host.exe");

    public static string AppExe { get; } = Path.Combine(AppDir, "OpenDownloadManager.exe");

    /// <summary>
    /// The colour icon the user chose in Settings: "orbit" or "smoke". A development copy starts on
    /// Smoke so it stands apart from the installed app. Set by ViewSettings.
    /// </summary>
    public static string AppIcon { get; set; } = IsDev ? "smoke" : "orbit";

    /// <summary>The chosen icon for windows, the taskbar and Alt+Tab.</summary>
    public static string IconPath => IconFile(AppIcon, "ico");

    /// <summary>The chosen icon at 256 px, for pictures of it in the app.</summary>
    public static string IconPngPath => IconFile(AppIcon, "png");

    public static string IconFile(string icon, string extension) =>
        Path.Combine(AppDir, "Assets", icon == "smoke" ? $"app-smoke.{extension}" : $"app.{extension}");

    /// <summary>
    /// The one-colour glyph of the chosen icon, for the tray and the tool windows' title bars: a dark
    /// glyph for a light taskbar, a white one for a dark taskbar.
    /// </summary>
    public static string TrayIconPath(bool lightTaskbar) =>
        Path.Combine(AppDir, "Assets", $"tray{(AppIcon == "smoke" ? "-smoke" : "")}-{(lightTaskbar ? "light" : "dark")}.ico");

    /// <summary>The user's Downloads folder, wherever they moved it.</summary>
    public static string DownloadsDir { get; } = Profile is null ? KnownDownloads() : Path.Combine(DataDir, "Downloads");

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
