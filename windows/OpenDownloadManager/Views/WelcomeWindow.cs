using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenDownloadManager.Core;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>
/// Shown on the first start: a few choices (where files go, light or dark, start with Windows),
/// then the browsers on this PC, each with a button that adds the bundled extension.
/// </summary>
public sealed class WelcomeWindow : ToolWindow
{
    private readonly CoreClient _core;
    private readonly AppSettings _settings;
    private readonly StackPanel _first, _second;
    private readonly Button _back = new() { Content = "Back" };
    private readonly Button _next = new() { Content = "Next" };
    private readonly Button _skip = new() { Content = "Skip" };
    private readonly TextBlock _page = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    public WelcomeWindow(CoreClient core, AppSettings settings) : base("Welcome", 620)
    {
        _core = core;
        _settings = settings;

        // ----- page 1: welcome and a few choices -----
        var hero = new Grid { ColumnSpacing = 16 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppPaths.AppDir, "Assets", "app.png"))), Width = 56, Height = 56 });
        var heroText = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = "Welcome to Open Download Manager", Style = Res<Style>("SubtitleTextBlockStyle"), TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = "Fast downloads that pause and resume any time, and videos from the sites you use.",
                            Style = ThemedStyles.Text(secondary: true),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                };
        Grid.SetColumn(heroText, 1);
        hero.Children.Add(heroText);

        var change = new Button { Content = "Change…" };
        change.Click += async (_, _) => await PickFolder();
        var folderRow = new Grid { ColumnSpacing = 12 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(_folder);
        Grid.SetColumn(change, 1);
        folderRow.Children.Add(change);
        ShowFolder();

        var theme = new RadioButtons
        {
            MaxColumns = 3,
            Items = { "Match Windows", "Light", "Dark" },
            SelectedIndex = ViewSettings.Current.Theme switch { "light" => 1, "dark" => 2, _ => 0 },
        };
        theme.SelectionChanged += (_, _) => ViewSettings.Current.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };

        var startup = new ToggleSwitch { IsOn = settings.StartWithWindows, OnContent = "Start with Windows, ready for browser downloads", OffContent = "Start with Windows, ready for browser downloads" };
        startup.Toggled += (_, _) =>
        {
            _settings.StartWithWindows = startup.IsOn;
            try { _settings.Save(); StartupRegistration.Apply(startup.IsOn); } catch { /* shown in Settings */ }
        };

        _first = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                hero,
                Field("Save downloads to", folderRow),
                Field("Look", theme),
                startup,
            },
        };

        // ----- page 2: browsers -----
        _second = new StackPanel
        {
            Spacing = 14,
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = "Add the extension to your browsers", Style = Res<Style>("SubtitleTextBlockStyle") },
                new TextBlock
                {
                    Text = "It comes with the app. With it, the downloads you start in the browser, and the videos on the pages you visit, come here. You can add it later from Settings.",
                    Style = ThemedStyles.Text(secondary: true),
                    TextWrapping = TextWrapping.Wrap,
                },
                new BrowserList(),
            },
        };

        _back.Click += (_, _) => Show(1);
        _next.Click += (_, _) => { if (_second.Visibility == Visibility.Visible) Finish(); else Show(2); };
        _skip.Click += (_, _) => Finish();
        var footer = new Grid();
        footer.Children.Add(_page);
        footer.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _skip, _back, _next },
        });
        _page.Style = ThemedStyles.Text(caption: true, secondary: true);

        Content = new StackPanel
        {
            Padding = new Thickness(28, 24, 28, 24),
            Spacing = 24,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { new Grid { Children = { _first, _second } }, footer },
        };
        Closed += (_, _) => MarkDone();
        Show(1);
        Ready();
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static StackPanel Field(string label, FrameworkElement control) => new()
    {
        Spacing = 6,
        Children = { new TextBlock { Text = label, Style = Res<Style>("BodyStrongTextBlockStyle") }, control },
    };

    private void Show(int page)
    {
        _first.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _second.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = page == 2 ? "Finish" : "Next";
        _next.Style = Res<Style>("AccentButtonStyle");
        _page.Text = $"{page} of 2";
        FitHeight();
    }

    private void ShowFolder()
    {
        var reply = _core.Call(new { type = "getSettings" });
        var folder = reply["settings"]?["downloadDir"]?.GetValue<string>();
        _folder.Text = string.IsNullOrEmpty(folder) ? reply["defaultDownloadDir"]?.GetValue<string>() ?? "" : folder;
        ToolTipService.SetToolTip(_folder, _folder.Text);
    }

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var reply = await _core.CallAsync(new { type = "getSettings" });
        if (reply["settings"] is not JsonObject settings) return;
        settings["downloadDir"] = folder.Path;
        await _core.CallAsync(new { type = "setSettings", settings });
        ShowFolder();
    }

    private void Finish()
    {
        MarkDone();
        Close();
    }

    private void MarkDone()
    {
        if (_settings.WelcomeDone) return;
        _settings.WelcomeDone = true;
        try { _settings.Save(); } catch { /* shown again next time; harmless */ }
    }
}
