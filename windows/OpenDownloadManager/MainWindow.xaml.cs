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

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (SelectAll.IsChecked == true) List.SelectAll();
        else List.SelectedItems.Clear();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var n = List.SelectedItems.Count;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 checked" : $"{n} checked";
        SelectAll.IsChecked = n == 0 ? false : n == ViewModel.Visible.Count ? true : null;
    }

    // ----- responsive layout -----

    /// <summary>Narrow windows put search and view options on their own row.</summary>
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 860;
        Grid.SetRow(Tools, narrow ? 1 : 0);
        Grid.SetColumn(Tools, narrow ? 0 : 1);
        Grid.SetColumnSpan(Tools, narrow ? 2 : 1);
        Tools.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Search.Width = narrow ? Math.Max(140, e.NewSize.Width - 340) : 220;
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

    private IEnumerable<DownloadItem> Selected => List.SelectedItems.OfType<DownloadItem>().ToList();

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

    private async void OnRemoveClick(object sender, RoutedEventArgs e) => await Remove(Selected.ToList(), deleteFile: false);

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

    private async void OnItemSpeedLimitClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var box = new NumberBox
        {
            Header = "Speed limit for this download (KB/s, 0 for none)",
            Minimum = 0,
            Value = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var dialog = new ContentDialog
        {
            Title = item.DisplayName,
            Content = box,
            PrimaryButtonText = "Set",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var limit = double.IsNaN(box.Value) ? 0 : (long)box.Value * 1024;
            await Call(new { type = "setJobSpeedLimit", id = item.Id, limit });
        }
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
