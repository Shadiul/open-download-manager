using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.Views;

/// <summary>
/// One download's own window, like IDM's: big progress, a map of what each connection has fetched,
/// the details, and Pause, Cancel and Hide. It turns into a "download complete" view at the end.
/// Closing or minimizing it does not stop the download; the list opens it again.
/// </summary>
public sealed class DownloadWindow : Window
{
    private readonly CoreClient _core;
    private readonly DownloadItem _item;
    private readonly DispatcherQueueTimer _timer;

    private readonly TextBlock _heading = new() { Style = Res<Style>("CaptionTextBlockStyle"), Opacity = 0.75 };
    private readonly TextBlock _name = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _url = new() { Style = Res<Style>("CaptionTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Opacity = 0.7 };
    private readonly FontIcon _glyph = new() { FontSize = 24, Foreground = new SolidColorBrush(Colors.White) };
    private readonly TextBlock _percent = new() { FontSize = 34, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _speed = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ProgressBar _bar = new() { Maximum = 100, Height = 6, MinHeight = 6 };
    private readonly Canvas _map = new() { Height = 14 };
    private readonly Border _mapFrame;
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly StackPanel _parts = new() { Spacing = 6 };
    private readonly Expander _connections;
    private readonly Button _pause = new() { MinWidth = 104 };
    private readonly Button _cancel = new() { Content = "Cancel" };
    private readonly Button _folder = new() { Content = "Show in folder" };
    private readonly Button _open = new() { Content = "Open", Visibility = Visibility.Collapsed };
    private readonly Button _hide = new() { Content = "Hide" };
    private JsonArray? _segments;
    private long _total;
    private bool _polling;

    public ulong Id => _item.Id;

    public DownloadWindow(CoreClient core, DownloadItem item)
    {
        _core = core;
        _item = item;
        SystemBackdrop = new MicaBackdrop();

        var tile = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = Res<Brush>("AccentFillColorDefaultBrush"),
            Child = _glyph,
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { _heading, _name, _url } };
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(tile);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var big = new Grid();
        big.Children.Add(_percent);
        big.Children.Add(_speed);

        _mapFrame = new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = Res<Brush>("ControlStrokeColorDefaultBrush"),
            Child = _map,
        };
        _mapFrame.SizeChanged += (_, _) => DrawMap();
        ToolTipService.SetToolTip(_mapFrame, "What each connection has fetched");

        var info = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] fields = ["Status", "Size", "Downloaded", "Time left", "Connections", "Can resume", "Saved to", "Added"];
        for (var i = 0; i < fields.Length; i++)
        {
            if (i % 2 == 0) info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var value = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            _values[fields[i]] = value;
            var cell = new StackPanel
            {
                Spacing = 1,
                Children = { new TextBlock { Text = fields[i], Style = Res<Style>("CaptionTextBlockStyle"), Opacity = 0.65 }, value },
            };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            info.Children.Add(cell);
        }

        _connections = new Expander
        {
            Header = "Connections",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 170, Content = _parts, Padding = new Thickness(0, 0, 12, 0) },
        };

        _pause.Click += async (_, _) => await TogglePause();
        _cancel.Click += async (_, _) => await CancelDownload();
        _folder.Click += (_, _) => Shell.ShowInFolder(_item.FilePath, _item.SaveDir);
        _open.Click += (_, _) => { if (_item.FilePath is { } p) Shell.Open(p); Close(); };
        _hide.Click += (_, _) => Close();
        ToolTipService.SetToolTip(_hide, "Close this window; the download carries on");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        _open.Style = Res<Style>("AccentButtonStyle");
        foreach (var b in new[] { _open, _pause, _cancel, _folder, _hide }) buttons.Children.Add(b);

        var body = new StackPanel
        {
            Padding = new Thickness(24, 18, 24, 20),
            Spacing = 14,
            Children = { header, big, _bar, _mapFrame, info, _connections, buttons },
        };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 560));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.PreferredMinimumWidth = 460;
            p.PreferredMinimumHeight = 420;
        }
        ViewSettings.Current.Track(this);

        _item.PropertyChanged += OnItemChanged;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(700);
        _timer.Tick += async (_, _) => await Poll();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _item.PropertyChanged -= OnItemChanged;
        };
        Refresh();
        _ = Poll();
        _timer.Start();
    }

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update() raises many properties at once; refresh on the last one it raises.
        if (e.PropertyName == nameof(DownloadItem.StatusBrush)) Refresh();
    }

    private void Refresh()
    {
        var i = _item;
        var done = i.IsCompleted;
        _heading.Text = done ? "Download complete" : i.IsFailed ? "Download failed" : i.IsActive ? "Downloading" : "Download";
        _name.Text = i.DisplayName;
        _url.Text = i.Url;
        ToolTipService.SetToolTip(_url, i.Url);
        _glyph.Glyph = done ? "" : i.Glyph;
        _percent.Text = i.TotalSize is > 0 || done ? $"{i.Progress:0}%" : Format.Bytes(i.Downloaded);
        _speed.Text = i.SpeedText;
        _bar.Value = i.Progress;
        _bar.IsIndeterminate = i.IsIndeterminate;
        _bar.ShowPaused = i.IsPaused;
        _bar.ShowError = i.IsFailed;

        _values["Status"].Text = i.IsFailed && i.Error is { } err ? $"{i.StatusText}: {err}" : i.StatusText;
        _values["Status"].Foreground = i.StatusBrush;
        ToolTipService.SetToolTip(_values["Status"], i.Error);
        _values["Size"].Text = i.TotalSize is > 0 ? Format.Bytes(i.TotalSize.Value) : "Unknown";
        _values["Downloaded"].Text = i.TotalSize is > 0 ? $"{Format.Bytes(i.Downloaded)} ({i.Progress:0.0}%)" : Format.Bytes(i.Downloaded);
        _values["Time left"].Text = i.TimeLeftText is { Length: > 0 } t ? t : done ? "Done" : "—";
        _values["Connections"].Text = i.Kind == "video" ? "Handled by yt-dlp" : i.Connections.ToString();
        _values["Can resume"].Text = i.Kind == "video" ? "Yes (yt-dlp continues)" : i.Resumable ? "Yes" : i.Downloaded > 0 ? "No, a pause starts over" : "Not known yet";
        _values["Saved to"].Text = i.FilePath ?? i.SaveDir;
        ToolTipService.SetToolTip(_values["Saved to"], i.FilePath ?? i.SaveDir);
        _values["Added"].Text = i.AddedText;

        _pause.Content = i.CanResume ? "Resume" : "Pause";
        _pause.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        _open.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        _hide.Content = done ? "Close" : "Hide";
        _connections.Visibility = i.Kind == "video" ? Visibility.Collapsed : Visibility.Visible;
        _mapFrame.Visibility = i.Kind == "video" ? Visibility.Collapsed : Visibility.Visible;

        var percent = i.TotalSize is > 0 && !done ? $"{i.Progress:0}% " : "";
        Title = done ? $"Done: {i.DisplayName}" : $"{percent}{i.DisplayName}";
        if (done) _timer?.Stop();
    }

    private async Task Poll()
    {
        if (_polling || _item.Kind == "video") return;
        _polling = true;
        try
        {
            var reply = await _core.CallAsync(new { type = "details", id = _item.Id });
            if (reply["ok"]?.GetValue<bool>() != true) return;
            _segments = reply["segments"] as JsonArray;
            _total = reply["total"] is JsonValue t && t.TryGetValue<long>(out var n) ? n : 0;
            DrawMap();
            DrawParts();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>The whole file as a bar: each connection's fetched part filled, at true scale.</summary>
    private void DrawMap()
    {
        _map.Children.Clear();
        var width = _mapFrame.ActualWidth;
        if (_segments is null || _total <= 0 || width <= 0) return;
        var fill = Res<Brush>("AccentFillColorDefaultBrush");
        foreach (var s in _segments.OfType<JsonObject>())
        {
            var start = s["start"]!.GetValue<long>();
            var cursor = s["cursor"]!.GetValue<long>();
            if (cursor <= start) continue;
            var x = start * width / _total;
            var w = Math.Max(1, (cursor - start) * width / _total);
            var rect = new Rectangle { Width = w, Height = 14, Fill = fill };
            Canvas.SetLeft(rect, x);
            _map.Children.Add(rect);
            // A thin gap marks where one connection's range ends and the next begins.
            var end = s["end"]!.GetValue<long>();
            if (end < _total)
            {
                var tick = new Rectangle { Width = 1, Height = 14, Fill = Res<Brush>("SolidBackgroundFillColorBaseBrush"), Opacity = 0.7 };
                Canvas.SetLeft(tick, end * width / _total);
                _map.Children.Add(tick);
            }
        }
    }

    /// <summary>One line per connection's range: fetched of its size.</summary>
    private void DrawParts()
    {
        var segs = _segments?.OfType<JsonObject>().ToList() ?? [];
        while (_parts.Children.Count > segs.Count) _parts.Children.RemoveAt(_parts.Children.Count - 1);
        for (var k = 0; k < segs.Count; k++)
        {
            var start = segs[k]["start"]!.GetValue<long>();
            var cursor = segs[k]["cursor"]!.GetValue<long>();
            var end = segs[k]["end"]!.GetValue<long>();
            var size = Math.Max(1, end - start);
            if (k >= _parts.Children.Count) _parts.Children.Add(PartRow());
            var row = (Grid)_parts.Children[k];
            ((TextBlock)row.Children[0]).Text = $"Part {k + 1}";
            ((ProgressBar)row.Children[1]).Value = (cursor - start) * 100.0 / size;
            ((TextBlock)row.Children[2]).Text = $"{Format.Bytes(cursor - start)} of {Format.Bytes(size)}";
        }
        _connections.Header = segs.Count == 0 ? "Connections" : $"Connections ({segs.Count} parts)";
    }

    private static Grid PartRow()
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var label = new TextBlock { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
        var bar = new ProgressBar { Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(label);
        row.Children.Add(bar);
        row.Children.Add(text);
        return row;
    }

    private async Task TogglePause()
    {
        var resume = _item.CanResume;
        await _core.CallAsync(new { type = resume ? "resume" : "pause", id = _item.Id });
        if (resume) _timer.Start();
    }

    private async Task CancelDownload()
    {
        var dialog = new ContentDialog
        {
            Title = "Cancel this download?",
            Content = "The download is removed from the list and the part already fetched is deleted.",
            PrimaryButtonText = "Cancel download",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _core.CallAsync(new { type = "remove", id = _item.Id, deleteFile = false });
        Close();
    }

    public void ShowOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }
}
