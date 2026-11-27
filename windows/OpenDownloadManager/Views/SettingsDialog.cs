using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenDownloadManager.Core;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>Engine settings (stored by the core) and app settings (stored next to them).</summary>
public sealed class SettingsDialog : ContentDialog
{
    private readonly CoreClient _core;
    private readonly AppSettings _app;
    private readonly IntPtr _hwnd;
    private JsonObject _settings = [];

    private readonly TextBox _folder = new() { Header = "Download folder" };
    private readonly ToggleSwitch _categories = new() { Header = "Sort into Video, Music, Documents, Programs, Archives and Other" };
    private readonly NumberBox _connections = Number("Connections per download (1 to 32)", 1, 32);
    private readonly NumberBox _parallel = Number("Downloads at the same time", 1, 20);
    private readonly NumberBox _speed = Number("Speed limit for all downloads (KB/s, 0 for none)", 0, 10_000_000);
    private readonly ToggleSwitch _schedule = new() { Header = "Only download between set times" };
    private readonly TimePicker _start = new() { Header = "From", ClockIdentifier = "24HourClock" };
    private readonly TimePicker _stop = new() { Header = "Until", ClockIdentifier = "24HourClock" };
    private readonly ComboBox _whenDone = new() { Header = "When all downloads finish", Items = { "Do nothing", "Sleep", "Shut down" } };
    private readonly ToggleSwitch _updateTools = new() { Header = "Update yt-dlp every day (sites change often)" };
    private readonly ToggleSwitch _startWithWindows = new() { Header = "Start with Windows, in the notification area" };
    private readonly ToggleSwitch _closeToTray = new() { Header = "Closing the window keeps downloads running in the notification area" };
    private readonly ToggleSwitch _clipboard = new() { Header = "Offer to download links I copy" };
    private readonly ToggleSwitch _notifications = new() { Header = "Notify me when a download finishes or fails" };
    private readonly TextBlock _hostStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _toolsStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    public SettingsDialog(CoreClient core, AppSettings app, IntPtr hwnd)
    {
        _core = core;
        _app = app;
        _hwnd = hwnd;
        Title = "Settings";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) => await PickFolder();
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(_folder);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);

        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _start, _stop } };
        _schedule.Toggled += (_, _) => times.Visibility = _schedule.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var reRegister = new Button { Content = "Register again" };
        reRegister.Click += (_, _) =>
        {
            try { NativeHostRegistration.Register(); } catch (Exception e) { _hostStatus.Text = e.Message; return; }
            ShowHostStatus();
        };
        var openTools = new HyperlinkButton { Content = "Open tools folder" };
        openTools.Click += (_, _) => { if (Directory.Exists(AppPaths.ToolsDir)) Process.Start("explorer.exe", $"\"{AppPaths.ToolsDir}\""); };

        Content = new ScrollViewer
        {
            MaxHeight = 560,
            Content = new StackPanel
            {
                Spacing = 14,
                MinWidth = 480,
                Padding = new Thickness(0, 0, 16, 0),
                Children =
                {
                    Heading("Files"), folderRow, _categories,
                    Heading("Speed"), _connections, _parallel, _speed,
                    Heading("Schedule"), _schedule, times, _whenDone,
                    Heading("Browser and video"), _hostStatus, reRegister, _updateTools, _toolsStatus, openTools,
                    Heading("App"), _startWithWindows, _closeToTray, _clipboard, _notifications,
                    new TextBlock { Text = $"Open Download Manager {typeof(App).Assembly.GetName().Version?.ToString(3)} · GPL-3.0", Opacity = 0.6 },
                },
            },
        };

        Load();
        times.Visibility = _schedule.IsOn ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonClick += OnSave;
    }

    private static NumberBox Number(string header, double min, double max) => new()
    {
        Header = header,
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Left,
        MinWidth = 200,
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, 0),
    };

    private void Load()
    {
        var reply = _core.Call(new { type = "getSettings" });
        _settings = reply["settings"] as JsonObject ?? [];
        var folder = _settings["downloadDir"]?.GetValue<string>() ?? "";
        _folder.Text = string.IsNullOrEmpty(folder) ? reply["defaultDownloadDir"]?.GetValue<string>() ?? "" : folder;
        _categories.IsOn = _settings["categoryFolders"]?.GetValue<bool>() ?? true;
        _connections.Value = _settings["connections"]?.GetValue<int>() ?? 8;
        _parallel.Value = _settings["maxParallel"]?.GetValue<int>() ?? 3;
        _speed.Value = (_settings["speedLimit"]?.GetValue<long>() ?? 0) / 1024;
        var schedule = _settings["schedule"] as JsonObject;
        _schedule.IsOn = schedule?["enabled"]?.GetValue<bool>() ?? false;
        _start.Time = ParseTime(schedule?["start"]?.GetValue<string>(), new TimeSpan(23, 0, 0));
        _stop.Time = ParseTime(schedule?["stop"]?.GetValue<string>(), new TimeSpan(7, 0, 0));
        _whenDone.SelectedIndex = (_settings["whenDone"]?.GetValue<string>()) switch { "sleep" => 1, "shutdown" => 2, _ => 0 };
        _updateTools.IsOn = _settings["autoUpdateTools"]?.GetValue<bool>() ?? true;

        _startWithWindows.IsOn = _app.StartWithWindows;
        _closeToTray.IsOn = _app.CloseToTray;
        _clipboard.IsOn = _app.WatchClipboard;
        _notifications.IsOn = _app.Notifications;
        ShowHostStatus();
        _toolsStatus.Text = File.Exists(Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe"))
            ? "yt-dlp, ffmpeg and deno are installed for video downloads."
            : "yt-dlp is missing, so video pages cannot be downloaded. Run scripts\\fetch-tools.ps1 when building from source.";
    }

    private void ShowHostStatus() => _hostStatus.Text = NativeHostRegistration.IsRegistered()
        ? "The browser extension can reach this app (registered for Chrome and Edge)."
        : "The browser extension cannot reach this app yet. Register it again.";

    private static TimeSpan ParseTime(string? hhmm, TimeSpan fallback) =>
        TimeSpan.TryParse(hhmm, out var t) ? t : fallback;

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) _folder.Text = folder.Path;
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        static long N(NumberBox b, long fallback) => double.IsNaN(b.Value) ? fallback : (long)b.Value;
        var settings = new
        {
            downloadDir = _folder.Text.Trim(),
            categoryFolders = _categories.IsOn,
            connections = N(_connections, 8),
            maxParallel = N(_parallel, 3),
            speedLimit = N(_speed, 0) * 1024,
            schedule = new
            {
                enabled = _schedule.IsOn,
                start = _start.Time.ToString(@"hh\:mm"),
                stop = _stop.Time.ToString(@"hh\:mm"),
            },
            whenDone = _whenDone.SelectedIndex switch { 1 => "sleep", 2 => "shutdown", _ => "nothing" },
            autoUpdateTools = _updateTools.IsOn,
        };
        var reply = await _core.CallAsync(new { type = "setSettings", settings });
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            args.Cancel = true;
            _toolsStatus.Text = reply["error"]?.GetValue<string>() ?? "Could not save the settings.";
            deferral.Complete();
            return;
        }

        _app.StartWithWindows = _startWithWindows.IsOn;
        _app.CloseToTray = _closeToTray.IsOn;
        _app.WatchClipboard = _clipboard.IsOn;
        _app.Notifications = _notifications.IsOn;
        try
        {
            _app.Save();
            StartupRegistration.Apply(_app.StartWithWindows);
        }
        catch
        {
            // Saved engine settings still apply; app settings fall back to their defaults next start.
        }
        deferral.Complete();
    }
}
