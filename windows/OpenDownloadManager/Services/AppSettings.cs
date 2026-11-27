using System.Text.Json;

namespace OpenDownloadManager.Services;

/// <summary>Settings that belong to the Windows app rather than the engine.</summary>
public sealed class AppSettings
{
    public bool WatchClipboard { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "app-settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // A damaged file falls back to the defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
