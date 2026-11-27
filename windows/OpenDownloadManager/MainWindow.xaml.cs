using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using OpenDownloadManager.ViewModels;
using OpenDownloadManager.Views;
using Windows.ApplicationModel.DataTransfer;

namespace OpenDownloadManager;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 720;
            p.PreferredMinimumHeight = 460;
        }

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ErrorText) && ViewModel.ErrorText is { } message)
                ShowError(message);
            if (e.PropertyName is nameof(MainViewModel.SortBy) or nameof(MainViewModel.SortDescending))
                ShowSortArrows();
            if (e.PropertyName == nameof(MainViewModel.CheckedCount)) ShowChecked();
        };
        ViewSettings.Current.Track(this);
        ShowSortArrows();
    }

    // ----- search, filters, view options -----

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ViewModel.SearchText = sender.Text;

    private void OnStatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusBox.SelectedItem is ComboBoxItem { Tag: string tag }) ViewModel.StatusFilter = tag;
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string column }) ViewModel.SortOn(column);
    }

    /// <summary>A small chevron on the sorted column's header, pointing the sort direction.</summary>
    private void ShowSortArrows()
    {
        var glyph = ViewModel.SortDescending ? "\uE70D" : "\uE70E";
        void Set(FontIcon icon, string key)
        {
            icon.Glyph = glyph;
            icon.Visibility = ViewModel.SortBy == key ? Visibility.Visible : Visibility.Collapsed;
        }
        Set(SortName, "name");
        Set(SortSize, "size");
        Set(SortProgress, "progress");
        Set(SortSpeed, "speed");
        Set(SortAdded, "added");
    }

    private void OnViewMenuOpening(object? sender, object e)
    {
        var v = ViewSettings.Current;
        ColSize.IsChecked = v.ShowSize;
        ColProgress.IsChecked = v.ShowProgress;
        ColSpeed.IsChecked = v.ShowSpeed;
        ColTimeLeft.IsChecked = v.ShowTimeLeft;
        ColAdded.IsChecked = v.ShowAdded;
        DensityCompact.IsChecked = v.Density == "compact";
        DensityStandard.IsChecked = v.Density == "standard";
        DensityComfortable.IsChecked = v.Density == "comfortable";
        ThemeSystem.IsChecked = v.Theme == "system";
        ThemeLight.IsChecked = v.Theme == "light";
        ThemeDark.IsChecked = v.Theme == "dark";
        ProgressWindows.IsChecked = ViewModel.AppSettings.ShowProgressWindow;
    }

    private void OnColumnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string column } item) return;
        var v = ViewSettings.Current;
        switch (column)
        {
            case "size": v.ShowSize = item.IsChecked; break;
            case "progress": v.ShowProgress = item.IsChecked; break;
            case "speed": v.ShowSpeed = item.IsChecked; break;
            case "timeLeft": v.ShowTimeLeft = item.IsChecked; break;
            case "added": v.ShowAdded = item.IsChecked; break;
        }
    }

    private void OnDensityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string density }) ViewSettings.Current.Density = density;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string theme }) ViewSettings.Current.Theme = theme;
    }

    private void OnProgressWindowsToggle(object sender, RoutedEventArgs e)
    {
        ViewModel.AppSettings.ShowProgressWindow = ProgressWindows.IsChecked;
        try { ViewModel.AppSettings.Save(); } catch { }
    }

    // ----- selection -----

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ViewModel.CheckAll(SelectAll.IsChecked == true);

    private void ShowChecked()
    {
        var n = ViewModel.CheckedCount;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 checked · Esc to clear" : $"{n} checked · Esc to clear";
        SelectAll.IsChecked = n == 0 ? false : n >= ViewModel.Visible.Count ? true : null;
    }

    /// <summary>
    /// A click only highlights the row, unless something is already checked: then it checks or
    /// unchecks the row, as in Gmail or Files.
    /// </summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.CheckedCount > 0 && e.ClickedItem is DownloadItem item) item.IsChecked = !item.IsChecked;
    }

    /// <summary>Space checks the highlighted row; Esc clears all checks.</summary>
    private void OnListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space && List.SelectedItem is DownloadItem item)
        {
            item.IsChecked = !item.IsChecked;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.CheckedCount > 0)
        {
            ViewModel.CheckAll(false);
            e.Handled = true;
        }
    }

    // ----- responsive layout -----

    /// <summary>Narrow windows put search and view options on their own row.</summary>
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 860;
        // When space is short the actions keep their icons and lose their labels, rather than
        // disappearing into the overflow menu.
        Actions.DefaultLabelPosition = e.NewSize.Width < 1080 && !narrow || e.NewSize.Width < 480
            ? CommandBarDefaultLabelPosition.Collapsed
            : CommandBarDefaultLabelPosition.Right;
        Grid.SetRow(Tools, narrow ? 1 : 0);
        Grid.SetColumn(Tools, narrow ? 0 : 1);
        Grid.SetColumnSpan(Tools, narrow ? 2 : 1);
        Tools.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Search.Width = narrow ? Math.Max(140, e.NewSize.Width - 460) : e.NewSize.Width < 1080 ? 160 : 220;
        if (Tools.Parent != Body && narrow)
        {
            ((Panel)Tools.Parent).Children.Remove(Tools);
            Body.Children.Add(Tools);
        }
        else if (Tools.Parent == Body && !narrow)
        {
            Body.Children.Remove(Tools);
            Toolbar.Children.Add(Tools);
        }
    }

    /// <summary>The list's width decides which columns fit.</summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewSettings.Current.SetListWidth(e.NewSize.Width);

    // ----- the row fill's shimmer -----

    /// <summary>Starts the shimmer that sweeps across a row's fill while bytes arrive.</summary>
    private void OnRowFillLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid fill || fill.Children.Count == 0 || fill.Children[0] is not Microsoft.UI.Xaml.Shapes.Rectangle shine) return;
        if (shine.RenderTransform is not Microsoft.UI.Xaml.Media.TranslateTransform move) return;
        var clip = new Microsoft.UI.Xaml.Media.RectangleGeometry();
        fill.Clip = clip;
        void Resize()
        {
            clip.Rect = new Windows.Foundation.Rect(0, 0, fill.ActualWidth, fill.ActualHeight);
            var sweep = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = -shine.Width,
                To = Math.Max(fill.ActualWidth, 200),
                Duration = TimeSpan.FromSeconds(1.8),
                RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            };
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sweep, move);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sweep, "X");
            story.Children.Add(sweep);
            story.Begin();
        }
        fill.SizeChanged += (_, _) => Resize();
        Resize();
    }

    // ----- progress windows -----

    private void OnShowProgressClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) App.Current?.ShowProgressWindow(item);
    }

    public void ShowError(string message) => ShowInfo(message, InfoBarSeverity.Error);

    public void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
    }

    public void BringToFront() => SetForegroundWindow(Hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>The checked downloads, or the highlighted one when nothing is checked.</summary>
    private IEnumerable<DownloadItem> Selected =>
        ViewModel.CheckedCount > 0 ? ViewModel.Checked
        : List.SelectedItem is DownloadItem one ? [one] : [];

    private static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    private async Task Call(object command)
    {
        var reply = await ViewModel.Core.CallAsync(command);
        if (reply["ok"]?.GetValue<bool>() != true)
            ShowError(reply["error"]?.GetValue<string>() ?? "The engine refused that.");
    }

    private SettingsPage? _settingsPage;

    /// <summary>Settings is a full page in place of the list; any other item shows the list again.</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            _settingsPage ??= new SettingsPage(ViewModel.Core, ViewModel.AppSettings, Hwnd);
            Nav.Content = _settingsPage;
            return;
        }
        Nav.Content = Body;
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) ViewModel.Filter = tag;
    }

    public async void OpenAddDialog(string? url, bool probe = false)
    {
        var dialog = new AddDownloadDialog(ViewModel.Core, Hwnd, url, probe) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Error is { } error) ShowError(error);
    }

    /// <summary>Counts down before sleep or shutdown; returns false if the user cancels.</summary>
    public async Task<bool> ConfirmCountdown(string action, int seconds)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            Title = "All downloads are done",
            Content = text,
            PrimaryButtonText = "Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var left = seconds;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        var timedOut = false;
        timer.Tick += (_, _) =>
        {
            left--;
            text.Text = $"The computer will {action} in {left} seconds.";
            if (left <= 0)
            {
                timedOut = true;
                timer.Stop();
                dialog.Hide();
            }
        };
        text.Text = $"The computer will {action} in {left} seconds.";
        timer.Start();
        var result = await dialog.ShowAsync();
        timer.Stop();
        return timedOut || result == ContentDialogResult.Primary;
    }

    private async void OnAddClick(object sender, RoutedEventArgs e) => OpenAddDialog(await ClipboardUrl());

    private static async Task<string?> ClipboardUrl()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return null;
            var text = (await content.GetTextAsync()).Trim();
            return Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) await Call(new { type = "resume", id = item.Id });
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) await Call(new { type = "pause", id = item.Id });
    }

    /// <summary>
    /// Remove asks first: take the downloads off the list and keep the files, or delete the files too.
    /// Unfinished partial files are deleted either way, since nothing can use them.
    /// </summary>
    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var items = Selected.ToList();
        if (items.Count == 0)
        {
            ShowInfo("Check the downloads to remove, or click one first.");
            return;
        }
        var what = items.Count == 1 ? $"\u201c{items[0].DisplayName}\u201d" : $"{items.Count} downloads";
        var finished = items.Count(i => i.IsCompleted);
        var dialog = new ContentDialog
        {
            Title = items.Count == 1 ? "Remove this download?" : $"Remove {items.Count} downloads?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = finished == 0
                    ? $"{what} will be removed from the list and the part already downloaded deleted."
                    : $"Remove {what} from the list only, or also delete the downloaded file{(finished == 1 ? "" : "s")} from your computer? Unfinished parts are deleted either way.",
            },
            PrimaryButtonText = "Remove from list",
            SecondaryButtonText = finished > 0 ? "Delete files too" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        await Remove(items, deleteFile: choice == ContentDialogResult.Secondary);
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e) => await Call(new { type = "resumeAll" });

    private async void OnPauseAllClick(object sender, RoutedEventArgs e) => await Call(new { type = "pauseAll" });

    private async void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) await Call(new { type = "move", id = item.Id, delta = -1 });
    }

    private async void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected.Reverse()) await Call(new { type = "move", id = item.Id, delta = 1 });
    }

    private async void OnClearCompletedClick(object sender, RoutedEventArgs e) => await Call(new { type = "clearCompleted" });

    /// <summary>A finished download opens its file; any other opens its progress window.</summary>
    private void OnItemDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (item is { IsCompleted: true, FilePath: { } path }) Shell.Open(path);
        else App.Current?.ShowProgressWindow(item);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { FilePath: { } path }) Shell.Open(path);
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Shell.ShowInFolder(item.FilePath, item.SaveDir);
    }

    private async void OnItemResumeClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await Call(new { type = "resume", id = item.Id });
    }

    private async void OnItemPauseClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await Call(new { type = "pause", id = item.Id });
    }

    /// <summary>
    /// Expired links (signed CDN addresses) get a new address from the browser: the next link
    /// the user clicks there is given to this download instead of starting a new one.
    /// </summary>
    private async void OnRefreshAddressClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        await Call(new { type = "refreshAddress", id = item.Id });
        ShowInfo($"Click the download link for \"{item.DisplayName}\" again in your browser. "
            + "The new address goes to this download, which then continues where it stopped.");
        if (!string.IsNullOrEmpty(item.PageUrl) && Uri.TryCreate(item.PageUrl, UriKind.Absolute, out var page))
            await Windows.System.Launcher.LaunchUriAsync(page);
    }

    /// <summary>
    /// A speed limit for one download: on or off, an amount, and its unit (MB/s by default), with
    /// a few common amounts one click away.
    /// </summary>
    private async void OnItemSpeedLimitClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        const long KB = 1024, MB = 1024 * 1024;
        var current = item.SpeedLimit;
        var inMb = current == 0 || current >= MB;

        var limitOn = new ToggleSwitch { Header = "Limit the speed of this download", IsOn = current > 0, OnContent = "On", OffContent = "Off" };
        var amount = new NumberBox
        {
            Minimum = 1,
            Maximum = 100_000,
            Value = current > 0 ? Math.Round(current / (double)(inMb ? MB : KB), 1) : 5,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            Width = 120,
            AcceptsExpression = false,
        };
        var unit = new ComboBox { Items = { "MB/s", "KB/s" }, SelectedIndex = inMb ? 0 : 1, Width = 100 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { amount, unit } };

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var mb in new[] { 1, 5, 10, 25, 50 })
        {
            var b = new Button { Content = $"{mb} MB/s", Padding = new Thickness(10, 4, 10, 5), FontSize = 12 };
            b.Click += (_, _) => { amount.Value = mb; unit.SelectedIndex = 0; limitOn.IsOn = true; };
            presets.Children.Add(b);
        }
        var limitPanel = new StackPanel { Spacing = 10, Children = { row, presets } };
        void Sync() => limitPanel.Opacity = limitOn.IsOn ? 1 : 0.45;
        limitOn.Toggled += (_, _) => { limitPanel.IsHitTestVisible = limitOn.IsOn; Sync(); };
        limitPanel.IsHitTestVisible = limitOn.IsOn;
        Sync();

        var dialog = new ContentDialog
        {
            Title = "Speed limit",
            Content = new StackPanel
            {
                Spacing = 14,
                MinWidth = 380,
                Children =
                {
                    new TextBlock { Text = item.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.75 },
                    limitOn,
                    limitPanel,
                    new TextBlock
                    {
                        Text = "Applies right away. The limit for all downloads together is in Settings.",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Opacity = 0.75,
                    },
                },
            },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var value = double.IsNaN(amount.Value) ? 0 : amount.Value;
        var limit = limitOn.IsOn && value > 0 ? (long)(value * (unit.SelectedIndex == 0 ? MB : KB)) : 0;
        await Call(new { type = "setJobSpeedLimit", id = item.Id, limit });
    }

    private void OnCopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var data = new DataPackage();
        data.SetText(item.Url);
        Clipboard.SetContent(data);
    }

    private async void OnItemRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await Remove([item], deleteFile: false);
    }

    private async void OnItemDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var dialog = new ContentDialog
        {
            Title = "Delete the file?",
            Content = $"\"{item.DisplayName}\" will be deleted from your computer, not only removed from the list.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Remove([item], deleteFile: true);
    }

    private async Task Remove(List<DownloadItem> items, bool deleteFile)
    {
        foreach (var item in items) await Call(new { type = "remove", id = item.Id, deleteFile });
    }
}
