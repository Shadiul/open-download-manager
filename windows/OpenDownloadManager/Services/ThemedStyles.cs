using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OpenDownloadManager.Services;

/// <summary>
/// Styles for views built in code. Their colours are {ThemeResource} setters, which resolve against
/// each element's own theme. Looking a brush up in Application.Resources gives the app-wide (system)
/// theme instead, which is wrong when the user picks Light or Dark in View.
/// </summary>
public static class ThemedStyles
{
    private static readonly Dictionary<string, Style> Cache = [];

    public static Style Text(bool caption = false, bool secondary = false) => Get(
        "TextBlock",
        caption ? "CaptionTextBlockStyle" : "BodyTextBlockStyle",
        ("Foreground", secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"));

    /// <summary>A card: the default fill, or the secondary (recessed) one.</summary>
    public static Style Card(bool secondary = false) => Get(
        "Border", null,
        ("BorderBrush", "CardStrokeColorDefaultBrush"),
        ("Background", secondary ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"));

    public static Style Get(string type, string? basedOn, params (string Property, string Key)[] setters)
    {
        var id = $"{type}|{basedOn}|{string.Join(",", setters)}";
        if (Cache.TryGetValue(id, out var cached)) return cached;
        var xaml = new System.Text.StringBuilder(
            $"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"{type}\"");
        if (basedOn is not null) xaml.Append($" BasedOn=\"{{StaticResource {basedOn}}}\"");
        xaml.Append('>');
        foreach (var (property, key) in setters) xaml.Append($"<Setter Property=\"{property}\" Value=\"{{ThemeResource {key}}}\"/>");
        xaml.Append("</Style>");
        return Cache[id] = (Style)XamlReader.Load(xaml.ToString());
    }
}
