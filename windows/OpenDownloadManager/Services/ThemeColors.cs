using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenDownloadManager.Services;

/// <summary>
/// Status colours for brushes made in code (list rows, the graph), in the theme the user picked in
/// View. Application.Resources would give the system theme's colours, which are unreadable when the
/// two differ (pale text on a light window, or dark text on a dark one). Values are Fluent's.
/// </summary>
public static class ThemeColors
{
    private static readonly UISettings Ui = new();
    private static readonly Dictionary<(bool, string), SolidColorBrush> Cache = [];

    /// <summary>Raised when the effective theme changes; brushes must be fetched again.</summary>
    public static event Action? Changed;

    public static bool IsDark => ViewSettings.Current.ElementTheme switch
    {
        ElementTheme.Dark => true,
        ElementTheme.Light => false,
        _ => SystemIsDark,
    };

    /// <summary>
    /// Windows' app mode as it is now. Application.RequestedTheme keeps the mode the app started
    /// in, so after Windows switches it would give dark colours on a light window (or the reverse).
    /// Windows' text colour follows the switch: light text means dark mode.
    /// </summary>
    private static bool SystemIsDark => Ui.GetColorValue(UIColorType.Foreground).R > 128;

    /// <summary>
    /// Whether the taskbar and tray are light. That is Windows' own mode, which can differ from the
    /// apps' mode (Settings, Personalization, Colors, Custom). A dark taskbar when the setting is missing.
    /// </summary>
    public static bool TaskbarIsLight
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Raised on the UI thread when Windows' colours change, the taskbar's mode included.</summary>
    public static event Action? SystemColorsChanged;

    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    /// <summary>Call once on the UI thread: Windows switching light/dark (or its accent) repaints the colours.</summary>
    public static void Watch()
    {
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Ui.ColorValuesChanged += (_, _) => _ui?.TryEnqueue(() =>
        {
            if (Appearance.CustomAccent is null) Appearance.ApplyAccent();
            else RaiseChanged();
            SystemColorsChanged?.Invoke();
        });
    }

    public static void RaiseChanged()
    {
        Cache.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The App icon choice changed: the tray icon (on SystemColorsChanged) and the tool windows'
    /// title-bar glyphs (on Changed) are drawn again from AppPaths.TrayIconPath.
    /// </summary>
    public static void RaiseIconChanged()
    {
        RaiseChanged();
        SystemColorsChanged?.Invoke();
    }

    /// <summary>The accent's shades: the user's pick, or Windows' own.</summary>
    public static Color AccentShade(UIColorType shade) =>
        Appearance.CustomAccent is { } c ? Appearance.Shade(c, shade) : Ui.GetColorValue(shade);

    public static Color Accent => IsDark ? AccentShade(UIColorType.AccentLight2) : AccentShade(UIColorType.AccentDark1);
    public static Color AccentText => IsDark ? AccentShade(UIColorType.AccentLight3) : AccentShade(UIColorType.AccentDark2);

    /// <summary>A colour for each kind of file, for rows coloured by type.</summary>
    public static Color ForCategory(string category) => (category, IsDark) switch
    {
        ("Video", false) => Color.FromArgb(255, 0x88, 0x17, 0x98),
        ("Video", true) => Color.FromArgb(255, 0xD5, 0x9D, 0xFF),
        ("Music", false) => Color.FromArgb(255, 0xBF, 0x00, 0x77),
        ("Music", true) => Color.FromArgb(255, 0xFF, 0x8D, 0xC8),
        ("Documents", false) => Color.FromArgb(255, 0x0F, 0x6C, 0xBD),
        ("Documents", true) => Color.FromArgb(255, 0x62, 0xAB, 0xF5),
        ("Programs", false) => Color.FromArgb(255, 0x03, 0x83, 0x87),
        ("Programs", true) => Color.FromArgb(255, 0x4C, 0xD5, 0xCC),
        ("Archives", false) => Color.FromArgb(255, 0xCA, 0x50, 0x10),
        ("Archives", true) => Color.FromArgb(255, 0xFF, 0xAA, 0x6C),
        (_, false) => Color.FromArgb(255, 0x60, 0x5E, 0x5C),
        (_, true) => Color.FromArgb(255, 0xC8, 0xC6, 0xC4),
    };
    public static Color Critical => IsDark ? Color.FromArgb(255, 0xFF, 0x99, 0xA4) : Color.FromArgb(255, 0xC4, 0x2B, 0x1C);
    public static Color Success => IsDark ? Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Color.FromArgb(255, 0x0F, 0x7B, 0x0F);
    public static Color Caution => IsDark ? Color.FromArgb(255, 0xFC, 0xE1, 0x00) : Color.FromArgb(255, 0x9D, 0x5D, 0x00);
    public static Color SecondaryText => IsDark ? Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x00, 0x00, 0x00);

    /// <summary>A shared brush of a colour above, optionally faded to <paramref name="alpha"/>.</summary>
    public static SolidColorBrush Brush(Func<Color> color, string name, byte alpha = 255)
    {
        var key = (IsDark, $"{name}/{alpha}");
        if (Cache.TryGetValue(key, out var b)) return b;
        var c = color();
        if (alpha != 255) c = Color.FromArgb(alpha, c.R, c.G, c.B);
        return Cache[key] = new SolidColorBrush(c);
    }
}
