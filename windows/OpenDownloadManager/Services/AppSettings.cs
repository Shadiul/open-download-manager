using System.Text.Json;

namespace OpenDownloadManager.Services;

/// <summary>Settings that belong to the Windows app rather than the engine.</summary>
public sealed class AppSettings
{
    public bool WatchClipboard { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    /// <summary>Open a progress window for each download that starts.</summary>
    public bool ShowProgressWindow { get; set; } = true;

    // Appearance and list layout (see ViewSettings).
    public string Theme { get; set; } = "system";
    public string Density { get; set; } = "standard";
    public bool ShowSize { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowSpeed { get; set; } = true;
    public bool ShowTimeLeft { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool ShowAdded { get; set; } = true;
    public string SortBy { get; set; } = "queue";
    public bool SortDescending { get; set; }

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
