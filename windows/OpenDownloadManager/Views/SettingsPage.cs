using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Core;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>
/// The settings, as a full page of Fluent settings cards. Every change is saved as it is made:
/// engine settings go to the core, app settings to app-settings.json.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly CoreClient _core;
    private readonly AppSettings _app;
    private readonly IntPtr _hwnd;
    private JsonObject _settings = [];
    private bool _loading = true;
    private readonly DispatcherTimerLike _saveSoon;

    private readonly TextBox _folder = new() { MinWidth = 260 };
    private readonly ToggleSwitch _categories = Switch();
    private readonly NumberBox _connections = Number(1, 32);
    private readonly NumberBox _parallel = Number(1, 20);
    private readonly NumberBox _speed = Number(0, 10_000_000);
    private readonly ComboBox _speedUnit = new() { Items = { "MB/s", "KB/s" }, SelectedIndex = 0, MinWidth = 100 };
    private readonly ToggleSwitch _schedule = Switch();
    private readonly TimePicker _start = new() { ClockIdentifier = "24HourClock", MinWidth = 0 };
    private readonly TimePicker _stop = new() { ClockIdentifier = "24HourClock", MinWidth = 0 };
    private readonly ComboBox _whenDone = new() { Items = { "Do nothing", "Sleep", "Shut down" }, MinWidth = 160 };
    private readonly ToggleSwitch _confirm = Switch();
    private readonly ToggleSwitch _progressWindows = Switch();
    private readonly ToggleSwitch _updateTools = Switch();
    private readonly ComboBox _theme = new() { Items = { "Use system setting", "Light", "Dark" }, MinWidth = 180 };
    private readonly ComboBox _density = new() { Items = { "Compact", "Standard", "Comfortable" }, MinWidth = 180 };
    private readonly ToggleSwitch _startWithWindows = Switch();
    private readonly ToggleSwitch _closeToTray = Switch();
    private readonly ToggleSwitch _clipboard = Switch();
    private readonly ToggleSwitch _notifications = Switch();
    private readonly TextBlock _hostStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _toolsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };

    public SettingsPage(CoreClient core, AppSettings app, IntPtr hwnd)
    {
        _core = core;
        _app = app;
        _hwnd = hwnd;
        _saveSoon = new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromMilliseconds(500), SaveEngine);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await PickFolder();
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _folder, browse } };

        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center }, _stop } };

        var reRegister = new Button { Content = "Register again" };
        reRegister.Click += (_, _) =>
        {
            try { NativeHostRegistration.Register(); } catch (Exception e) { ShowError(e.Message); }
            ShowHostStatus();
        };
        var openTools = new Button { Content = "Open folder" };
        openTools.Click += (_, _) => OpenFolder(AppPaths.ToolsDir);
        var openData = new Button { Content = "Open folder" };
        openData.Click += (_, _) => OpenFolder(AppPaths.DataDir);

        var page = new StackPanel { Spacing = 4, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(new TextBlock { Text = "Settings", Style = Res<Style>("TitleTextBlockStyle"), Margin = new Thickness(0, 4, 0, 12) });
        page.Children.Add(_error);

        Group(page, "Downloads");
        page.Children.Add(Card("", "Download folder", "Where finished files go.", folderRow));
        page.Children.Add(Card("", "Sort into category folders", "Video, Music, Documents, Programs, Archives and Other, inside the download folder.", _categories));
        page.Children.Add(Card("", "Connections per download", "More connections are faster on most servers. 1 to 32.", _connections));
        page.Children.Add(Card("", "Downloads at the same time", "The rest wait in the queue.", _parallel));
        page.Children.Add(Card("", "Speed limit", "For all downloads together. 0 means no limit.",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _speed, _speedUnit } }));

        Group(page, "Schedule");
        page.Children.Add(Card("", "Only download between set times", "Downloads outside the window wait in the queue.", _schedule));
        page.Children.Add(Card("", "Download window", "Can run past midnight, such as 23:00 to 07:00.", times));
        page.Children.Add(Card("", "When all downloads finish", "Sleep and shut down wait 30 seconds so you can cancel.", _whenDone));

        Group(page, "Browser and video");
        page.Children.Add(Card("", "Ask before each browser download", "Shows the file name, size and folder before it starts.", _confirm));
        page.Children.Add(Card("", "Open a progress window for each download", "Like IDM. You can close it; the download carries on.", _progressWindows));
        page.Children.Add(Card("", "Browser extension", null, new StackPanel { Spacing = 8, Children = { _hostStatus, reRegister } }, wideContent: true));
        page.Children.Add(Card("", "Update yt-dlp every day", "Video sites change often; a current yt-dlp keeps working.", _updateTools));
        page.Children.Add(Card("", "Video tools", null, new StackPanel { Spacing = 8, Children = { _toolsStatus, openTools } }, wideContent: true));

        Group(page, "Appearance");
        page.Children.Add(Card("", "Theme", null, _theme));
        page.Children.Add(Card("", "Density", "How much space each download takes in the list.", _density));

        Group(page, "System");
        page.Children.Add(Card("", "Start with Windows", "Starts in the notification area, ready for browser downloads.", _startWithWindows));
        page.Children.Add(Card("", "Closing the window keeps downloads running", "The app stays in the notification area.", _closeToTray));
        page.Children.Add(Card("", "Offer to download links I copy", "For files, and for video pages such as YouTube in Chrome.", _clipboard));
        page.Children.Add(Card("", "Notifications", "When a download finishes or fails.", _notifications));

        Group(page, "About");
        page.Children.Add(Card("", $"Open Download Manager {typeof(App).Assembly.GetName().Version?.ToString(3)}", "Free and open source under GPL-3.0.", null));
        page.Children.Add(Card("", "Data folder", "Download history, settings and app.log.", openData));

        Content = new ScrollViewer
        {
            Content = new Grid { Padding = new Thickness(24, 8, 24, 32), Children = { page } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Load();
        Wire();
        _loading = false;
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static ToggleSwitch Switch() => new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };

    private static NumberBox Number(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        MinWidth = 140,
    };

    private static void Group(Panel page, string title) => page.Children.Add(new TextBlock
    {
        Text = title,
        Style = Res<Style>("BodyStrongTextBlockStyle"),
        Margin = new Thickness(1, 20, 0, 6),
    });

    /// <summary>A Fluent settings card: icon, title and description on the left, the control on the right.</summary>
    private static Border Card(string glyph, string title, string? description, FrameworkElement? control, bool wideContent = false)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, Style = ThemedStyles.Text(caption: true, secondary: true), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            if (wideContent)
            {
                // Status text and its button sit under the title.
                text.Children.Add(control);
                control.Margin = new Thickness(0, 6, 0, 0);
            }
            else
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Style = ThemedStyles.Card(),
        };
    }

    private void Load()
    {
        var reply = _core.Call(new { type = "getSettings" });
        _settings = reply["settings"] as JsonObject ?? [];
        var folder = _settings["downloadDir"]?.GetValue<string>() ?? "";
        _folder.Text = string.IsNullOrEmpty(folder) ? reply["defaultDownloadDir"]?.GetValue<string>() ?? "" : folder;
        _categories.IsOn = _settings["categoryFolders"]?.GetValue<bool>() ?? true;
        _connections.Value = _settings["connections"]?.GetValue<int>() ?? 8;
        _parallel.Value = _settings["maxParallel"]?.GetValue<int>() ?? 3;
        var limit = _settings["speedLimit"]?.GetValue<long>() ?? 0;
        var inMb = limit == 0 || limit % (1024 * 1024) == 0;
        _speedUnit.SelectedIndex = inMb ? 0 : 1;
        _speed.Value = limit / (inMb ? 1024.0 * 1024 : 1024.0);
        var schedule = _settings["schedule"] as JsonObject;
        _schedule.IsOn = schedule?["enabled"]?.GetValue<bool>() ?? false;
        _start.Time = TimeSpan.TryParse(schedule?["start"]?.GetValue<string>(), out var a) ? a : new TimeSpan(23, 0, 0);
        _stop.Time = TimeSpan.TryParse(schedule?["stop"]?.GetValue<string>(), out var b) ? b : new TimeSpan(7, 0, 0);
        _whenDone.SelectedIndex = _settings["whenDone"]?.GetValue<string>() switch { "sleep" => 1, "shutdown" => 2, _ => 0 };
        _updateTools.IsOn = _settings["autoUpdateTools"]?.GetValue<bool>() ?? true;
        _confirm.IsOn = _settings["confirmBrowserDownloads"]?.GetValue<bool>() ?? true;

        _progressWindows.IsOn = _app.ShowProgressWindow;
        _theme.SelectedIndex = ViewSettings.Current.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _density.SelectedIndex = ViewSettings.Current.Density switch { "compact" => 0, "comfortable" => 2, _ => 1 };
        _startWithWindows.IsOn = _app.StartWithWindows;
        _closeToTray.IsOn = _app.CloseToTray;
        _clipboard.IsOn = _app.WatchClipboard;
        _notifications.IsOn = _app.Notifications;
        ShowHostStatus();
        _toolsStatus.Text = File.Exists(Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe"))
            ? "yt-dlp, ffmpeg and deno are installed for video downloads."
            : "yt-dlp is missing, so video pages cannot be downloaded. Run scripts\\fetch-tools.ps1 when building from source.";
    }

    private void Wire()
    {
        void Engine(object? s, object? e) { if (!_loading) _saveSoon.Restart(); }
        _folder.LostFocus += (_, _) => Engine(null, null);
        _categories.Toggled += Engine;
        _connections.ValueChanged += (_, _) => Engine(null, null);
        _parallel.ValueChanged += (_, _) => Engine(null, null);
        _speed.ValueChanged += (_, _) => Engine(null, null);
        _speedUnit.SelectionChanged += (_, _) => Engine(null, null);
        _schedule.Toggled += Engine;
        _start.TimeChanged += (_, _) => Engine(null, null);
        _stop.TimeChanged += (_, _) => Engine(null, null);
        _whenDone.SelectionChanged += (_, _) => Engine(null, null);
        _updateTools.Toggled += Engine;
        _confirm.Toggled += Engine;

        _theme.SelectionChanged += (_, _) => { if (!_loading) ViewSettings.Current.Theme = _theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; };
        _density.SelectionChanged += (_, _) => { if (!_loading) ViewSettings.Current.Density = _density.SelectedIndex switch { 0 => "compact", 2 => "comfortable", _ => "standard" }; };
        _progressWindows.Toggled += (_, _) => SaveApp(s => s.ShowProgressWindow = _progressWindows.IsOn);
        _startWithWindows.Toggled += (_, _) => SaveApp(s => s.StartWithWindows = _startWithWindows.IsOn, () => StartupRegistration.Apply(_startWithWindows.IsOn));
        _closeToTray.Toggled += (_, _) => SaveApp(s => s.CloseToTray = _closeToTray.IsOn);
        _clipboard.Toggled += (_, _) => SaveApp(s => s.WatchClipboard = _clipboard.IsOn);
        _notifications.Toggled += (_, _) => SaveApp(s => s.Notifications = _notifications.IsOn);
    }

    private void SaveApp(Action<AppSettings> change, Action? then = null)
    {
        if (_loading) return;
        change(_app);
        try
        {
            _app.Save();
            then?.Invoke();
        }
        catch (Exception e)
        {
            ShowError($"Could not save the setting: {e.Message}");
        }
    }

    private async void SaveEngine()
    {
        static long N(NumberBox b, long fallback) => double.IsNaN(b.Value) ? fallback : (long)b.Value;
        // Only a full path is a usable folder; anything else keeps the saved one.
        var folder = _folder.Text.Trim();
        if (folder.Length > 0 && !IsFullPath(folder))
        {
            ShowError($"\"{folder}\" is not a full folder path, such as C:\\Users\\you\\Downloads. The download folder was not changed.");
            folder = _settings["downloadDir"]?.GetValue<string>() ?? "";
        }
        var settings = new JsonObject(_settings.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
        {
            ["downloadDir"] = folder,
            ["categoryFolders"] = _categories.IsOn,
            ["connections"] = N(_connections, 8),
            ["maxParallel"] = N(_parallel, 3),
            ["speedLimit"] = (long)((double.IsNaN(_speed.Value) ? 0 : _speed.Value) * (_speedUnit.SelectedIndex == 0 ? 1024 * 1024 : 1024)),
            ["schedule"] = new JsonObject
            {
                ["enabled"] = _schedule.IsOn,
                ["start"] = _start.Time.ToString(@"hh\:mm"),
                ["stop"] = _stop.Time.ToString(@"hh\:mm"),
            },
            ["whenDone"] = _whenDone.SelectedIndex switch { 1 => "sleep", 2 => "shutdown", _ => "nothing" },
            ["autoUpdateTools"] = _updateTools.IsOn,
            ["confirmBrowserDownloads"] = _confirm.IsOn,
        };
        var reply = await _core.CallAsync(new { type = "setSettings", settings });
        if (reply["ok"]?.GetValue<bool>() == true && reply["settings"] is JsonObject saved) _settings = saved;
        else ShowError(reply["error"]?.GetValue<string>() ?? "Could not save the settings.");
    }

    private static bool IsFullPath(string path)
    {
        try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0; }
        catch { return false; }
    }

    private void ShowHostStatus() => _hostStatus.Text = NativeHostRegistration.IsRegistered()
        ? "Chrome and Edge can reach this app."
        : "The browser extension cannot reach this app yet. Register it again.";

    private void ShowError(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _folder.Text = folder.Path;
        _saveSoon.Restart();
    }

    /// <summary>Runs an action once, a moment after the last call: one save for a burst of changes.</summary>
    private sealed class DispatcherTimerLike
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherTimerLike(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan delay, Action action)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => action();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }
    }
}
