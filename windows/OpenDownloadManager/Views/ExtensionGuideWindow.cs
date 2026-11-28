using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Services;
using Windows.ApplicationModel.DataTransfer;

namespace OpenDownloadManager.Views;

/// <summary>
/// Walks through adding the bundled extension to one browser. Browsers only install extensions
/// from outside their stores by hand, so the window opens the extensions page and the folder,
/// says what to do there, and turns to "Added" by itself once the browser has the extension.
/// </summary>
public sealed class ExtensionGuideWindow : ToolWindow
{
    private static readonly Dictionary<string, ExtensionGuideWindow> Open = [];

    private readonly Browser _browser;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _poll;
    private readonly ProgressRing _waiting = new() { IsActive = true, Width = 18, Height = 18 };
    private readonly Border _doneBadge;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _done = new() { Content = "Close" };

    /// <summary>Opens the guide for this browser (or brings it back), and starts the install.</summary>
    public static void Show(Browser browser)
    {
        if (!Open.TryGetValue(browser.Id, out var window))
        {
            window = new ExtensionGuideWindow(browser);
            Open[browser.Id] = window;
            window.Closed += (_, _) => Open.Remove(browser.Id);
        }
        window.ShowOnTop();
    }

    private ExtensionGuideWindow(Browser browser) : base($"Add to {browser.Name}", 560)
    {
        _browser = browser;
        var shortName = browser.Name.Split(' ')[^1];
        var devModeWhere = browser.Id switch
        {
            "chrome" => "at the top right of the page",
            "edge" => "in the left pane of the page",
            _ => "on the extensions page",
        };

        var icon = new Image { Width = 32, Height = 32 };
        _ = LoadIcon(icon, browser.Exe);
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(icon);
        var headerText = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = $"Add the extension to {shortName}", Style = Res<Style>("SubtitleTextBlockStyle") },
                new TextBlock
                {
                    Text = $"{shortName} adds extensions from outside its store only by hand: two steps on its extensions page, which opens with the folder beside it.",
                    Style = ThemedStyles.Text(caption: true, secondary: true),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        Grid.SetColumn(headerText, 1);
        header.Children.Add(headerText);

        var path = new TextBox { Text = browser.ExtensionFolder, IsReadOnly = true, FontSize = 12 };
        var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(copy, "Copy the folder's path");
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(browser.ExtensionFolder);
            Clipboard.SetContent(data);
        };
        var pathRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(path);
        Grid.SetColumn(copy, 1);
        pathRow.Children.Add(copy);

        var steps = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Step(1, "Turn on Developer mode", $"It is a switch {devModeWhere}.", null),
                Step(2, $"Drag the selected “{Path.GetFileName(browser.ExtensionFolder)}” folder from File Explorer onto the page",
                    "Or choose Load unpacked and pick this folder:", pathRow),
            },
        };

        _doneBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ThemeColors.Brush(() => ThemeColors.Success, "success"),
            Visibility = Visibility.Collapsed,
            Child = new FontIcon { Glyph = "", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
        var statusRow = new Grid { ColumnSpacing = 10 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.Children.Add(new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { _waiting, _doneBadge } });
        Grid.SetColumn(_status, 1);
        statusRow.Children.Add(_status);
        var statusCard = new Border
        {
            Child = statusRow,
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Card(secondary: true),
        };

        var again = new Button { Content = "Open the extensions page" };
        again.Click += (_, _) => _browser.StartInstall();
        var folder = new Button { Content = "Show the folder" };
        folder.Click += (_, _) => _browser.ShowFolder();
        _done.Click += (_, _) => Close();
        var footer = new Grid();
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { again, folder } });
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _done } });

        Content = new StackPanel
        {
            Padding = new Thickness(24, 20, 24, 24),
            Spacing = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { header, steps, statusCard, footer },
        };
        Ready();

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(1.5);
        _poll.Tick += (_, _) => Check();
        Closed += (_, _) => _poll.Stop();
        ShowWaiting();
        if (!_browser.HasExtension()) _browser.StartInstall();
        Check();
        _poll.Start();
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static async Task LoadIcon(Image image, string exe) => image.Source = await FileIcons.ForAsync(exe, exe, 64);

    private static Grid Step(int number, string title, string detail, FrameworkElement? extra)
    {
        var bubble = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Style = ThemedStyles.Get("Border", null, ("Background", "AccentFillColorDefaultBrush")),
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = ThemedStyles.Get("TextBlock", null, ("Foreground", "TextOnAccentFillColorPrimaryBrush")),
            },
        };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = detail, Style = ThemedStyles.Text(caption: false, secondary: true), TextWrapping = TextWrapping.Wrap });
        if (extra is not null) text.Children.Add(extra);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(bubble);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private void ShowWaiting()
    {
        _waiting.Visibility = Visibility.Visible;
        _doneBadge.Visibility = Visibility.Collapsed;
        _status.Text = $"Waiting for {_browser.Name.Split(' ')[^1]} to add it…";
        _done.Content = "Close";
        _done.Style = null;
    }

    private void Check()
    {
        if (!_browser.HasExtension()) return;
        _poll.Stop();
        _waiting.Visibility = Visibility.Collapsed;
        _doneBadge.Visibility = Visibility.Visible;
        _status.Text = $"Added to {_browser.Name.Split(' ')[^1]}. It connects to this app by itself; downloads you start there now come here.";
        _done.Content = "Done";
        _done.Style = Res<Style>("AccentButtonStyle");
        FitHeight();
        BrowserList.RefreshAll();
    }
}
