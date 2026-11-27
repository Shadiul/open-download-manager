using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using Windows.Storage.Pickers;

namespace OpenDownloadManager.Views;

/// <summary>
/// Add a link. For video pages (YouTube and other sites yt-dlp knows) it lists the qualities first,
/// the same menu the extension shows next to the player.
/// </summary>
public sealed class AddDownloadDialog : ContentDialog
{
    private readonly CoreClient _core;
    private readonly IntPtr _hwnd;
    private readonly TextBox _url = new() { Header = "Address", PlaceholderText = "https://…" };
    private readonly TextBox _name = new() { Header = "File name (optional)" };
    private readonly TextBox _folder = new() { Header = "Save to", PlaceholderText = "Default download folder" };
    private readonly ComboBox _quality = new() { Header = "Quality", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _find = new() { Content = "Find qualities" };
    private readonly StackPanel _findRow = new() { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly CheckBox _startNow = new() { Content = "Start now", IsChecked = true };
    private List<JsonObject> _formats = [];
    private string? _title;

    /// <summary>Set when adding failed after the dialog closed.</summary>
    public string? Error { get; private set; }

    public AddDownloadDialog(CoreClient core, IntPtr hwnd, string? url, bool probe)
    {
        _core = core;
        _hwnd = hwnd;
        Title = "Add download";
        PrimaryButtonText = "Download";
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

        _findRow.Children.Add(_find);
        _findRow.Children.Add(_busy);

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children = { _url, _findRow, _quality, _note, _name, folderRow, _startNow },
        };

        _url.Text = url ?? "";
        _url.TextChanged += (_, _) => UpdateKind();
        _find.Click += async (_, _) => await Probe();
        PrimaryButtonClick += OnPrimary;
        UpdateKind();
        if (probe && IsVideo) Loaded += async (_, _) => await Probe();
    }

    private bool IsVideo => VideoSites.IsVideoPage(_url.Text);

    private void UpdateKind()
    {
        _findRow.Visibility = IsVideo ? Visibility.Visible : Visibility.Collapsed;
        _name.Visibility = IsVideo ? Visibility.Collapsed : Visibility.Visible;
        if (!IsVideo)
        {
            _quality.Visibility = Visibility.Collapsed;
            _formats = [];
        }
        SetNote(IsVideo && _formats.Count == 0 ? "This looks like a video page. Find qualities to choose one, or download the best quality." : "");
    }

    private void SetNote(string? text)
    {
        _note.Text = text ?? "";
        _note.Visibility = string.IsNullOrEmpty(_note.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task Probe()
    {
        _busy.IsActive = true;
        _find.IsEnabled = false;
        SetNote("Looking up the qualities…");
        var reply = await _core.CallAsync(new { type = "probe", url = _url.Text.Trim() });
        _busy.IsActive = false;
        _find.IsEnabled = true;
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            SetNote($"Could not list the qualities: {reply["error"]?.GetValue<string>()}. The best quality will be downloaded.");
            return;
        }
        _title = reply["title"]?.GetValue<string>();
        _formats = (reply["formats"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        _quality.Items.Clear();
        foreach (var f in _formats)
        {
            var size = f["size"] is JsonValue v && v.TryGetValue<long>(out var bytes) ? $" · {Format.Bytes(bytes)}" : "";
            _quality.Items.Add($"{f["label"]} ({f["ext"]}){size}");
        }
        _quality.Visibility = _formats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_formats.Count > 0) _quality.SelectedIndex = 0;
        SetNote(_title ?? "");
    }

    private async Task PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) _folder.Text = folder.Path;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var url = _url.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            args.Cancel = true;
            SetNote("Enter an http or https address.");
            return;
        }
        var deferral = args.GetDeferral();
        var video = IsVideo || VideoSites.IsStream(url);
        string? formatId = null;
        if (video && _quality.SelectedIndex >= 0 && _quality.SelectedIndex < _formats.Count)
            formatId = _formats[_quality.SelectedIndex]["id"]?.GetValue<string>();
        var reply = await _core.CallAsync(new
        {
            type = "add",
            url,
            fileName = video || string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim(),
            saveDir = string.IsNullOrWhiteSpace(_folder.Text) ? null : _folder.Text.Trim(),
            mediaKind = IsVideo ? "page" : VideoSites.IsStream(url) ? (url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? "dash" : "hls") : null,
            formatId,
            title = _title,
            start = _startNow.IsChecked == true,
        });
        if (reply["ok"]?.GetValue<bool>() != true)
        {
            args.Cancel = true;
            SetNote(reply["error"]?.GetValue<string>() ?? "Could not add the download.");
        }
        deferral.Complete();
    }
}
