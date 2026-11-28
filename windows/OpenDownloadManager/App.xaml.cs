using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;
using OpenDownloadManager.Core;
using OpenDownloadManager.Services;
using OpenDownloadManager.ViewModels;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;

namespace OpenDownloadManager;

public partial class App : Application
{
    public static new App? Current { get; private set; }

    private MainWindow? _window;
    private TaskbarIcon? _tray;
    private CoreClient? _core;
    private MainViewModel? _vm;
    private DispatcherQueue? _ui;
    private string _lastClipboard = "";
    private bool _exiting;
    private readonly Dictionary<ulong, Views.DownloadWindow> _progressWindows = [];

    public AppSettings Settings { get; } = AppSettings.Load();

    public App()
    {
        Current = this;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Error("unhandled", e.Exception);
            _window?.ShowError($"Something went wrong: {e.Message}");
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        ViewSettings.Current.Load(Settings);
        Appearance.ApplyAccent();
        ThemeColors.Watch();
        Notifications.Invoked += a => _ui.TryEnqueue(() => OnNotification(a));
        var profile = AppPaths.Profile;
        if (profile is null) Notifications.Register();

        try
        {
            _core = CoreClient.Start(new
            {
                dataDir = AppPaths.DataDir,
                defaultDownloadDir = AppPaths.DownloadsDir,
                toolsDir = AppPaths.ToolsDir,
                listen = true,
                endpoint = profile is null ? null : $@"\\.\pipe\OpenDownloadManager-{Environment.UserName}-{profile}",
            }, _ui);
        }
        catch (Exception e)
        {
            ShowFatal(e.Message);
            return;
        }

        if (profile is null)
        {
            try { NativeHostRegistration.Register(); } catch { /* shown in Settings as not registered */ }
            try { StartupRegistration.Apply(Settings.StartWithWindows); } catch { }
        }

        _vm = new MainViewModel(_core, Settings);
        _vm.Completed += OnCompleted;
        _vm.Failed += item =>
        {
            if (Settings.Notifications) Notifications.Failed(item.Id, item.DisplayName, item.Error ?? "");
        };
        _vm.QueueFinished += OnQueueFinished;
        _vm.NeedsConfirmation += ShowNewDownload;
        _vm.Started += item =>
        {
            // While the new-download window asks, it shows the progress itself; the progress window follows the answer.
            if (Settings.ShowProgressWindow && !item.AwaitingConfirmation) ShowProgressWindow(item);
        };
        _vm.Load();

        _window = new MainWindow(_vm);
        _window.AppWindow.Closing += (_, e) =>
        {
            if (_exiting || !Settings.CloseToTray) { Quit(); return; }
            e.Cancel = true;
            _window.AppWindow.Hide();
        };
        CreateTray();
        WatchClipboard();
        foreach (var waiting in _vm.AwaitingConfirmation.ToList()) ShowNewDownload(waiting);

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification
            && activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast)
        {
            OnNotification(toast.Arguments);
        }
        else if (!Environment.GetCommandLineArgs().Contains("--background"))
        {
            ShowWindow();
            // The first start offers the extension to each browser (and a few choices). The installer's
            // "Set up the browser extension" starts the app with --setup-extension to get to that step.
            var setupExtension = Environment.GetCommandLineArgs().Contains("--setup-extension");
            if (!Settings.WelcomeDone || setupExtension) ShowWelcome(browsers: setupExtension);
        }
        // The extension in the data folder follows the copy shipped with this version of the app.
        _ = Task.Run(() => { try { BundledExtension.Sync(); } catch (Exception e) { Log.Error("extension sync", e); } });
    }

    private Views.WelcomeWindow? _welcome;

    /// <param name="browsers">Open on the browsers page, unless the welcome has never been done.</param>
    public void ShowWelcome(bool browsers = false)
    {
        if (_core is null) return;
        if (_welcome is null)
        {
            _welcome = new Views.WelcomeWindow(_core, Settings);
            _welcome.Closed += (_, _) => _welcome = null;
        }
        if (browsers && Settings.WelcomeDone) _welcome.ShowBrowsers();
        _welcome.ShowOnTop();
    }

    /// <summary>A second launch handed over to this instance.</summary>
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        _ui?.TryEnqueue(() =>
        {
            if (args.Kind == ExtendedActivationKind.AppNotification
                && args.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast)
            {
                OnNotification(toast.Arguments);
                return;
            }
            var launchArgs = args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch
                ? launch.Arguments ?? ""
                : "";
            if (launchArgs.Contains("--quit")) { Quit(); return; }
            if (!launchArgs.Contains("--background")) ShowWindow();
            if (launchArgs.Contains("--setup-extension")) ShowWelcome(browsers: true);
        });
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        _window.Activate();
        _window.BringToFront();
    }

    public void Quit()
    {
        if (_exiting && _core is null) return;
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        if (AppPaths.Profile is null) Notifications.Unregister();
        Power.KeepAwake(false);
        SpeedHistory.Save();
        _core?.Dispose();
        _core = null;
        _window?.Close();
        Environment.Exit(0);
    }

    /// <summary>Opens the download's progress window, or brings it back if it is already open.</summary>
    public void ShowProgressWindow(Models.DownloadItem item)
    {
        if (_core is null) return;
        if (!_progressWindows.TryGetValue(item.Id, out var window))
        {
            window = new Views.DownloadWindow(_core, item);
            _progressWindows[item.Id] = window;
            window.Closed += (_, _) => _progressWindows.Remove(item.Id);
        }
        window.ShowOnTop();
    }

    private void ShowNewDownload(Models.DownloadItem item)
    {
        if (_core is null) return;
        try
        {
            new Views.NewDownloadWindow(_core, item).ShowOnTop();
        }
        catch (Exception e)
        {
            Log.Error("new-download window", e);
            // Without the window the download must not be stuck: start it.
            _window?.ShowError($"Could not show the new-download window: {e.Message}");
            _ = _core.CallAsync(new { type = "confirm", id = item.Id, start = true });
        }
    }

    private void CreateTray()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open Open Download Manager", ShowWindow));
        menu.Items.Add(Item("Add download…", () => { ShowWindow(); _window?.OpenAddDialog(null); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Pause all", () => _ = _core?.CallAsync(new { type = "pauseAll" })));
        menu.Items.Add(Item("Resume all", () => _ = _core?.CallAsync(new { type = "resumeAll" })));
        var whenDone = new MenuFlyoutSubItem { Text = "When downloads finish", Icon = new FontIcon { Glyph = "" } };
        menu.Items.Add(whenDone);
        menu.Opening += (_, _) =>
        {
            if (_core is not null) AfterDownload.FillMenu(whenDone.Items, _core, AfterDownload.QueueAction(_core));
        };
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Exit", Quit));

        _tray = new TaskbarIcon
        {
            ToolTipText = AppPaths.AppName,
            IconSource = TrayIcon(),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ShowWindow),
        };
        _tray.ForceCreate();
        // The glyph is one colour, so it follows the taskbar when Windows switches between light and dark.
        ThemeColors.SystemColorsChanged += () => { if (_tray is not null) _tray.IconSource = TrayIcon(); };

        static MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(action);
            return item;
        }
    }

    private static BitmapImage TrayIcon() => new(new Uri(AppPaths.TrayIconPath(ThemeColors.TaskbarIsLight)));

    public void UpdateTrayTooltip(string text)
    {
        if (_tray is not null) _tray.ToolTipText = text;
    }

    private void OnCompleted(Models.DownloadItem item)
    {
        var path = item.FilePath;
        if (!string.IsNullOrEmpty(path))
        {
            var (url, page) = (item.Url, item.PageUrl);
            _ = Task.Run(() => MarkOfTheWeb.Apply(path, url, page));
        }
        if (Settings.Notifications) Notifications.Completed(item.Id, item.DisplayName, item.SaveDir);
        AfterDownload.OnFileFinished(item, Settings);
    }

    private async void OnQueueFinished(string whenDone)
    {
        var action = AfterDownload.QueueActions.FirstOrDefault(a => a.Value == whenDone);
        if (_window is null || _core is null || action.Value is null or "nothing") return;
        ShowWindow();
        var go = await _window.ConfirmCountdown(action.Countdown, Math.Max(5, Settings.QueueActionDelay));
        // Armed once: it goes back to "Do nothing" whether it ran or was cancelled.
        if (Settings.QueueActionOnce) await AfterDownload.SetQueueAction(_core, "nothing");
        if (!go) return;
        if (whenDone is "shutdown" or "restart" or "signOut")
        {
            // Save everything before Windows closes the app.
            SpeedHistory.Save();
            _core?.Dispose();
            _core = null;
        }
        AfterDownload.Perform(whenDone, Quit);
    }

    private void OnNotification(IDictionary<string, string> args)
    {
        args.TryGetValue("action", out var action);
        ulong.TryParse(args.TryGetValue("id", out var id) ? id : "", out var jobId);
        var item = _vm?.Find(jobId);
        switch (action)
        {
            case "open" when item?.FilePath is { } path:
                Shell.Open(path);
                break;
            case "folder" when item is not null:
                Shell.ShowInFolder(item.FilePath, item.SaveDir);
                break;
            case "add" when args.TryGetValue("url", out var url):
                _ = _core?.CallAsync(new { type = "add", url });
                break;
            case "pick" when args.TryGetValue("url", out var url):
                ShowWindow();
                _window?.OpenAddDialog(url, probe: true);
                break;
            default:
                ShowWindow();
                break;
        }
    }

    /// <summary>
    /// Offers copied links that point at a file or a video page. This is also how Chrome users
    /// download from YouTube, since the Chrome build of the extension leaves YouTube alone.
    /// </summary>
    private void WatchClipboard()
    {
        Clipboard.ContentChanged += async (_, _) =>
        {
            if (!Settings.WatchClipboard) return;
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                var text = (await content.GetTextAsync()).Trim();
                if (text == _lastClipboard || text.Length > 2048 || text.Contains('\n')) return;
                _lastClipboard = text;
                if (Models.VideoSites.IsVideoPage(text)) Notifications.ClipboardOffer(text, video: true);
                else if (Models.VideoSites.LooksLikeFile(text)) Notifications.ClipboardOffer(text, video: false);
            }
            catch
            {
                // The clipboard is busy or holds something we cannot read.
            }
        };
    }

    private void ShowFatal(string message)
    {
        var w = new Window { Title = AppPaths.AppName };
        w.Content = new TextBlock
        {
            Text = $"Open Download Manager could not start.\n\n{message}",
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
        };
        w.Activate();
    }
}
