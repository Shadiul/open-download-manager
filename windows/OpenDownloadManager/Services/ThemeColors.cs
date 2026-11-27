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
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    public static void RaiseChanged() => Changed?.Invoke();

    public static Color Accent => IsDark ? Ui.GetColorValue(UIColorType.AccentLight2) : Ui.GetColorValue(UIColorType.AccentDark1);
    public static Color AccentText => IsDark ? Ui.GetColorValue(UIColorType.AccentLight3) : Ui.GetColorValue(UIColorType.AccentDark2);
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
