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
    public bool ShowAdded { get; set; } = true;
    public string SortBy { get; set; } = "queue";

    // Look (see Appearance and RowFill).
    /// <summary>"system" for the Windows accent, or a colour as #RRGGBB.</summary>
    public string Accent { get; set; } = "system";
    /// <summary>mica, micaAlt, acrylic or solid.</summary>
    public string Backdrop { get; set; } = "mica";
    /// <summary>Before 0.3.2: one style for everything (dots, solid, glass, stripes, underline, none,
    /// ring or bar). Read once to fill the four settings below when they are missing.</summary>
    public string ProgressStyle { get; set; } = "dots";
    /// <summary>Progress across the row: bar, underline or none.</summary>
    public string? RowProgress { get; set; }
    /// <summary>The row's look: dots, solid, glass or stripes.</summary>
    public string? RowLook { get; set; }
    /// <summary>Progress in the Progress column: ring, bar or text.</summary>
    public string? ColumnProgress { get; set; }
    /// <summary>The column's look: dots, solid, glass or stripes.</summary>
    public string? ColumnLook { get; set; }
    /// <summary>off, calm or lively: the band that sweeps along a downloading row.</summary>
    public string Animation { get; set; } = "calm";
    /// <summary>subtle, normal or vivid.</summary>
    public string FillStrength { get; set; } = "normal";
    /// <summary>status, or type (each kind of file in its own colour).</summary>
    public string ColorBy { get; set; } = "status";

    // After each download (see AfterDownload).
    public bool AfterOpen { get; set; }
    public bool AfterShowInFolder { get; set; }
    public bool AfterExtract { get; set; }
    public bool AfterExtractDelete { get; set; }

    // When the whole queue is done: the action itself is an engine setting (whenDone).
    /// <summary>Seconds to cancel before the computer sleeps, shuts down and so on.</summary>
    public int QueueActionDelay { get; set; } = 30;
    /// <summary>Go back to "Do nothing" after the action, so a shutdown is not left armed.</summary>
    public bool QueueActionOnce { get; set; } = true;

    /// <summary>The welcome window has been seen (it offers to add the extension to each browser).</summary>
    public bool WelcomeDone { get; set; }
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
