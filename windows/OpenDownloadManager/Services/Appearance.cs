using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace OpenDownloadManager.Services;

/// <summary>
/// The look the user picked beyond light and dark: the accent colour (Windows' own or a chosen
/// one, applied to buttons, switches and the rows), the window material, and the row style
/// (drawn by RowFill, which listens to <see cref="RowsChanged"/>).
/// </summary>
public static class Appearance
{
    /// <summary>Preset accents: name and colour.</summary>
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Ocean", "#0F6CBD"),
        ("Teal", "#038387"),
        ("Forest", "#107C10"),
        ("Violet", "#8764B8"),
        ("Orchid", "#B146C2"),
        ("Rose", "#E3008C"),
        ("Coral", "#D13438"),
        ("Amber", "#CA5010"),
        ("Graphite", "#525E6E"),
    ];

    private static readonly List<WeakReference<Window>> Windows = [];

    // The plain background follows light and dark.
    static Appearance() => ThemeColors.Changed += ApplyBackdrops;

    /// <summary>The row style, animation, fill strength or row colouring changed.</summary>
    public static event Action? RowsChanged;

    public static void RaiseRowsChanged() => RowsChanged?.Invoke();

    /// <summary>The chosen accent, or null for Windows' accent.</summary>
    public static Color? CustomAccent => TryParse(ViewSettings.Current.Accent);

    public static Color? TryParse(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#') return null;
        try
        {
            return Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// The Windows accent shades worked out from one colour, the way Windows does it: lighter
    /// shades move toward white, darker toward black, in steps.
    /// </summary>
    public static Color Shade(Color c, UIColorType shade) => shade switch
    {
        UIColorType.AccentLight1 => Mix(c, 255, 0.25),
        UIColorType.AccentLight2 => Mix(c, 255, 0.45),
        UIColorType.AccentLight3 => Mix(c, 255, 0.65),
        UIColorType.AccentDark1 => Mix(c, 0, 0.18),
        UIColorType.AccentDark2 => Mix(c, 0, 0.36),
        UIColorType.AccentDark3 => Mix(c, 0, 0.54),
        _ => c,
    };

    private static Color Mix(Color c, byte toward, double amount) => Color.FromArgb(255,
        (byte)Math.Round(c.R + (toward - c.R) * amount),
        (byte)Math.Round(c.G + (toward - c.G) * amount),
        (byte)Math.Round(c.B + (toward - c.B) * amount));

    /// <summary>
    /// Puts the accent into the app's resources, so Fluent controls (accent buttons, switches,
    /// selection) use it, then asks every window to look its theme resources up again.
    /// </summary>
    public static void ApplyAccent()
    {
        var resources = Application.Current.Resources;
        var names = new (string Key, UIColorType Shade)[]
        {
            ("SystemAccentColor", UIColorType.Accent),
            ("SystemAccentColorLight1", UIColorType.AccentLight1),
            ("SystemAccentColorLight2", UIColorType.AccentLight2),
            ("SystemAccentColorLight3", UIColorType.AccentLight3),
            ("SystemAccentColorDark1", UIColorType.AccentDark1),
            ("SystemAccentColorDark2", UIColorType.AccentDark2),
            ("SystemAccentColorDark3", UIColorType.AccentDark3),
        };
        var ui = new UISettings();
        foreach (var (key, shade) in names)
            resources[key] = CustomAccent is { } c ? Shade(c, shade) : ui.GetColorValue(shade);
        foreach (var w in Live())
        {
            // Theme resources are looked up again when the theme changes: flip it and back.
            if (w.Content is FrameworkElement root)
            {
                var theme = root.RequestedTheme;
                root.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = theme;
            }
        }
        ThemeColors.RaiseChanged();
    }

    /// <summary>The window material the user picked; null for a plain background.</summary>
    public static SystemBackdrop? Backdrop() => ViewSettings.Current.Backdrop switch
    {
        "micaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
        "acrylic" => new DesktopAcrylicBackdrop(),
        "solid" => null,
        _ => new MicaBackdrop(),
    };

    /// <summary>Keeps a window's material in step with the setting. Call once its content is set.</summary>
    public static void Track(Window window)
    {
        Windows.Add(new WeakReference<Window>(window));
        window.Closed += (_, _) => Windows.RemoveAll(r => !r.TryGetTarget(out var w) || w == window);
        ApplyBackdrop(window);
    }

    public static void ApplyBackdrops()
    {
        foreach (var w in Live()) ApplyBackdrop(w);
    }

    private static void ApplyBackdrop(Window window)
    {
        var solid = ViewSettings.Current.Backdrop == "solid";
        window.SystemBackdrop = Backdrop();
        if (window.Content is Panel panel)
        {
            if (solid) panel.Background = ThemeColors.Brush(() => ThemeColors.IsDark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3), "solidBase");
            else panel.ClearValue(Panel.BackgroundProperty);
        }
    }

    private static IEnumerable<Window> Live()
    {
        foreach (var r in Windows.ToList())
            if (r.TryGetTarget(out var w)) yield return w;
    }
}
