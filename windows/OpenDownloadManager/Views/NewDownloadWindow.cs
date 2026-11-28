using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>
/// The window that pops up when the browser hands over a download. The download is already
/// running behind it: the window offers another name or folder, Download later (pause) or Cancel.
/// </summary>
public sealed class NewDownloadWindow : Window
{
    private readonly CoreClient _core;
    private readonly DownloadItem _item;
    private readonly TextBox _name;
    private readonly TextBox _folder;
    private readonly CheckBox _dontAsk = new() { Content = "Don't ask again: start browser downloads right away" };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Colors.IndianRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly string _originalName;
    private readonly string _defaultFolder;
    private readonly string _baseFolder;
    private string _lastName;
    private readonly TextBlock _size = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    private readonly TextBlock _progress = new() { Style = ThemedStyles.Text(caption: true, secondary: true) };
    private string _originalNameShown = "";
    private bool _decided;
    private Button _start = null!, _later = null!;

    public NewDownloadWindow(CoreClient core, DownloadItem item)
    {
        _core = core;
        _item = item;
        Title = "New download";
        SystemBackdrop = Appearance.Backdrop();

        var video = item.Kind == "video";
        _originalName = video ? item.Title ?? item.FileName : item.FileName;
        // The download's folder changes once it settles its category; the choice starts from the base.
        _baseFolder = item.SaveDir;
        _lastName = _originalName;
        _defaultFolder = DefaultFolder(core, item, _baseFolder);

        _name = new TextBox { Header = video ? "Name" : "File name", Text = _originalName };
        _folder = new TextBox { Header = "Save to", Text = _defaultFolder };
        _name.TextChanged += (_, _) =>
        {
            // The category folder follows the name until the user picks a folder.
            if (!video && _folder.Text == _defaultFolderFor(_lastName))
                _folder.Text = _defaultFolderFor(_name.Text);
            _lastName = _name.Text;
        };

        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) => await PickFolder();
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(_folder);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 30 });
        header.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _size,
                new TextBlock
                {
                    // One line with the site; the full address (often a long signed link) is on hover.
                    Text = Uri.TryCreate(item.Url, UriKind.Absolute, out var source) ? $"From {source.Host}" : item.Url,
                    MaxWidth = 470,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxLines = 1,
                    Style = ThemedStyles.Text(caption: true, secondary: true),
                },
                _progress,
            },
        });
        ToolTipService.SetToolTip(header, item.Url);

        var start = _start = new Button { Content = "Download", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var later = _later = new Button { Content = "Download later" };
        var cancel = new Button { Content = "Cancel" };
        start.Click += async (_, _) => await Decide(startNow: true);
        later.Click += async (_, _) => await Decide(startNow: false);
        cancel.Click += async (_, _) => await CancelDownload();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(start);
        buttons.Children.Add(later);
        buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Padding = new Thickness(24, 16, 24, 20),
            Spacing = 14,
            Children = { header, _name, folderRow, _dontAsk, _error, buttons },
        };

        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 380));
        if (Content is FrameworkElement root) root.Loaded += (_, _) => FitHeight();
        // A dark title bar in dark mode, like the rest of the window.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        CenterOnScreen();
        ViewSettings.Current.Track(this);
        Appearance.Track(this);
        _originalNameShown = _originalName;
        ShowLive();
        _item.PropertyChanged += OnItemChanged;
        Closed += (_, _) => _item.PropertyChanged -= OnItemChanged;
        // Closing the window with its X is the same as Cancel.
        AppWindow.Closing += async (_, e) =>
        {
            if (_decided) return;
            e.Cancel = true;
            await CancelDownload();
        };
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(ShowLive);

    /// <summary>The size and progress of the download running behind the window.</summary>
    private void ShowLive()
    {
        var video = _item.Kind == "video";
        _size.Text = "Size: " + (_item.TotalSize is > 0 ? Format.Bytes(_item.TotalSize.Value)
            : video ? "known once the video's details arrive"
            : "unknown until the server answers");
        _progress.Text = _item.State switch
        {
            "connecting" => "Connecting…",
            "downloading" => string.Join(" · ", new[] { $"Downloading: {_item.ProgressText}", _item.SpeedText }.Where(t => t.Length > 0)),
            "completed" => "Downloaded. A new name or folder moves the file.",
            "failed" => _item.Error ?? "Failed",
            _ => "",
        };
        // Finished before the user answered: the main button keeps the choices and closes.
        var done = _item.State == "completed";
        if (_start is not null)
        {
            _later.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        }
        // The server's name, once known, replaces the guess, unless the user has typed their own.
        if (!video && _item.FileName != _originalNameShown && _name.Text == _originalNameShown)
        {
            _originalNameShown = _item.FileName;
            _name.Text = _item.FileName;
        }
    }

    /// <summary>Makes the window exactly as tall as its content, so the buttons always show.</summary>
    private void FitHeight()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        var scale = root.XamlRoot.RasterizationScale;
        root.Measure(new Windows.Foundation.Size(AppWindow.ClientSize.Width / scale, double.PositiveInfinity));
        var height = (int)Math.Ceiling(root.DesiredSize.Height * scale);
        if (height > 0 && height != AppWindow.ClientSize.Height)
            AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(AppWindow.ClientSize.Width, height));
    }

    private string _defaultFolderFor(string name) => DefaultFolder(_core, _item, _baseFolder, name);

    /// <summary>Where the file goes unless the user picks a folder: the base folder, plus its category.</summary>
    private static string DefaultFolder(CoreClient core, DownloadItem item, string baseFolder, string? name = null)
    {
        if (item.Kind == "video") return baseFolder;
        var settings = core.Call(new { type = "getSettings" })["settings"] as JsonObject;
        var categories = settings?["categoryFolders"]?.GetValue<bool>() ?? true;
        return categories ? Path.Combine(baseFolder, Categories.Of(name ?? item.FileName)) : baseFolder;
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 3));
    }

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) _folder.Text = folder.Path;
    }

    private async Task Decide(bool startNow)
    {
        var name = _name.Text.Trim();
        var folder = _folder.Text.Trim();
        if (name.Length == 0) { ShowError("Enter a file name."); return; }
        if (!Path.IsPathFullyQualified(folder)) { ShowError("Choose a full folder path, like C:\\Users\\you\\Downloads."); return; }
        var nameChanged = name.Length > 0 && name != _originalNameShown;
        // Only a folder the user changed is fixed; otherwise the category follows the server's file name.
        var folderChanged = folder.Length > 0 && folder != _defaultFolderFor(name);
        if (nameChanged || folderChanged)
        {
            var edited = await _core.CallAsync(new
            {
                type = "edit",
                id = _item.Id,
                fileName = nameChanged ? name : null,
                saveDir = folderChanged ? folder : null,
            });
            if (!Ok(edited)) return;
        }
        if (!Ok(await _core.CallAsync(new { type = "confirm", id = _item.Id, start = startNow }))) return;
        if (_dontAsk.IsChecked == true) await TurnOffAsking();
        Finish();
        // Finished while the question was open: show it as done. Otherwise its progress, if wanted.
        if (_item.State == "completed") App.Current?.ShowProgressWindow(_item);
        else if (startNow && App.Current?.Settings.ShowProgressWindow == true) App.Current.ShowProgressWindow(_item);
    }

    /// <summary>
    /// Cancel means the download is not wanted: it leaves the list, with whatever it fetched, even if it
    /// finished while the question was open.
    /// </summary>
    private async Task CancelDownload()
    {
        await _core.CallAsync(new { type = "remove", id = _item.Id, deleteFile = _item.State == "completed" });
        Finish();
    }

    private async Task TurnOffAsking()
    {
        var reply = await _core.CallAsync(new { type = "getSettings" });
        if (reply["settings"] is JsonObject settings)
        {
            settings["confirmBrowserDownloads"] = false;
            await _core.CallAsync(new { type = "setSettings", settings });
        }
    }

    private bool Ok(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == true) return true;
        var error = reply["error"]?.GetValue<string>() ?? "Something went wrong.";
        // Removed from the list meanwhile: there is nothing left to answer.
        if (error == "No such download") { Finish(); return false; }
        ShowError(error);
        return false;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        FitHeight();
    }

    private void Finish()
    {
        _decided = true;
        Close();
    }

    public void ShowOnTop()
    {
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
