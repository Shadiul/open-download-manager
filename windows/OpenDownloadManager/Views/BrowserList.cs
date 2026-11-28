using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.Views;

/// <summary>
/// Each browser on this PC with the extension's state in it: Added, or an Add button that opens
/// the guide. Used in Settings and in the welcome window.
/// </summary>
public sealed class BrowserList : StackPanel
{
    private static readonly List<WeakReference<BrowserList>> Lists = [];

    /// <summary>Re-reads every visible list, e.g. after a guide saw the extension arrive.</summary>
    public static void RefreshAll()
    {
        foreach (var r in Lists.ToList())
            if (r.TryGetTarget(out var list)) list.Refresh();
            else Lists.Remove(r);
    }

    public BrowserList()
    {
        Spacing = 6;
        Lists.Add(new WeakReference<BrowserList>(this));
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Children.Clear();
        var browsers = Browsers.Installed();
        if (browsers.Count == 0)
        {
            Children.Add(new TextBlock
            {
                Text = "No supported browser found. The extension works in Edge, Chrome, Brave, Vivaldi and Opera.",
                Style = ThemedStyles.Text(caption: false, secondary: true),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        if (!BundledExtension.IsBundled)
        {
            Children.Add(new TextBlock
            {
                Text = "This copy of the app was built without the extension. Build it with node scripts/build.mjs, or get it from the release page.",
                Style = ThemedStyles.Text(caption: false, secondary: true),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        foreach (var browser in browsers) Children.Add(Row(browser));
    }

    private static Border Row(Browser browser)
    {
        var added = browser.HasExtension();
        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        _ = LoadIcon(icon, browser.Exe);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (added)
        {
            status.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = ThemeColors.Brush(() => ThemeColors.Success, "success") });
            status.Children.Add(new TextBlock { Text = "Extension added", Style = ThemedStyles.Text(caption: true, secondary: true) });
        }
        else
        {
            status.Children.Add(new TextBlock { Text = "Extension not added yet", Style = ThemedStyles.Text(caption: true, secondary: true) });
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = browser.Name });
        text.Children.Add(status);

        var action = new Button { Content = added ? "Show steps" : "Add", MinWidth = 96, IsEnabled = BundledExtension.IsBundled };
        if (!added) action.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        action.Click += (_, _) => ExtensionGuideWindow.Show(browser);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(action, 2);
        action.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(action);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Card(secondary: true),
        };
    }

    private static async Task LoadIcon(Image image, string exe) => image.Source = await FileIcons.ForAsync(exe, exe, 64);
}
