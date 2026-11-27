using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace OpenDownloadManager.Models;

/// <summary>One row in the download list, updated from the core's job JSON.</summary>
public sealed partial class DownloadItem : ObservableObject
{
    public ulong Id { get; }

    [ObservableProperty] public partial string Kind { get; set; } = "http";
    [ObservableProperty] public partial string Url { get; set; } = "";
    [ObservableProperty] public partial string? PageUrl { get; set; }
    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string SaveDir { get; set; } = "";
    [ObservableProperty] public partial string? FilePath { get; set; }
    [ObservableProperty] public partial long? TotalSize { get; set; }
    [ObservableProperty] public partial long Downloaded { get; set; }
    [ObservableProperty] public partial long Speed { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "queued";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool LinkExpired { get; set; }
    [ObservableProperty] public partial bool Resumable { get; set; }
    [ObservableProperty] public partial int Connections { get; set; }
    [ObservableProperty] public partial long Position { get; set; }
    [ObservableProperty] public partial long CreatedAt { get; set; }
    /// <summary>This download's own speed limit in bytes per second; 0 for none.</summary>
    [ObservableProperty] public partial long SpeedLimit { get; set; }
    [ObservableProperty] public partial bool AwaitingConfirmation { get; set; }
    /// <summary>Checked in the list, for actions on several downloads at once.</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }
    /// <summary>The file's thumbnail or its type's icon, loaded in the background.</summary>
    [ObservableProperty] public partial ImageSource? Icon { get; set; }

    public bool NoIcon => Icon is null;
    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(NoIcon));
    private string _iconFor = "";

    /// <summary>Loads the icon again when the file's name or finished path changes.</summary>
    private async void LoadIcon()
    {
        var key = $"{FileName}|{FilePath}|{IsCompleted}";
        if (key == _iconFor || string.IsNullOrEmpty(FileName)) return;
        _iconFor = key;
        var icon = await Services.FileIcons.ForAsync(FileName, IsCompleted ? FilePath : null);
        if (_iconFor == key && icon is not null) Icon = icon;
    }

    public DownloadItem(ulong id) => Id = id;

    public static DownloadItem From(JsonObject job)
    {
        var item = new DownloadItem((ulong)(job["id"]?.GetValue<long>() ?? 0));
        item.Update(job);
        return item;
    }

    public void Update(JsonObject j)
    {
        Kind = Str(j, "kind") ?? "http";
        Url = Str(j, "url") ?? "";
        PageUrl = Str(j, "pageUrl");
        Title = Str(j, "title");
        FileName = Str(j, "fileName") ?? "";
        SaveDir = Str(j, "saveDir") ?? "";
        FilePath = Str(j, "filePath");
        TotalSize = Long(j, "totalSize");
        Downloaded = Long(j, "downloaded") ?? 0;
        Speed = Long(j, "speed") ?? 0;
        State = Str(j, "state") ?? "queued";
        Error = Str(j, "error");
        LinkExpired = j["linkExpired"]?.GetValue<bool>() ?? false;
        Resumable = j["resumable"]?.GetValue<bool>() ?? false;
        Connections = (int)(Long(j, "connections") ?? 0);
        Position = Long(j, "position") ?? 0;
        CreatedAt = Long(j, "createdAt") ?? 0;
        SpeedLimit = Long(j, "speedLimit") ?? 0;
        AwaitingConfirmation = j["awaitingConfirmation"]?.GetValue<bool>() ?? false;

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(TimeLeftText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(BarVisible));
        OnPropertyChanged(nameof(StateVisible));
        OnPropertyChanged(nameof(BarText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(FillScale));
        OnPropertyChanged(nameof(FillOpacity));
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(IsShimmering));
        LoadIcon();
    }

    // ----- the one progress-and-status column -----

    /// <summary>A bar while the download runs or is paused partway; a word otherwise.</summary>
    public bool BarVisible => State is "connecting" or "downloading" or "waitingToRetry"
        || (State == "paused" && !AwaitingConfirmation && Downloaded > 0);

    public bool StateVisible => !BarVisible;

    /// <summary>The progress column's text: "36%", "Paused 36%", "Done" and so on.</summary>
    public string ProgressText => BarVisible ? (State == "connecting" ? "Connecting" : BarText) : StateText;

    // ----- the row as a progress bar -----

    /// <summary>How far across the row the fill reaches (only X is used).</summary>
    public System.Numerics.Vector3 FillScale => new(
        (float)(IsCompleted ? 1 : BarVisible ? (TotalSize is > 0 ? Math.Max(0.004, Progress / 100) : 1) : IsFailed && Downloaded > 0 && TotalSize is > 0 ? Progress / 100 : 0), 1, 1);

    /// <summary>Faint without a known size; fades out once done.</summary>
    public double FillOpacity => IsCompleted ? 0 : BarVisible ? (TotalSize is > 0 ? 1 : 0.55) : IsFailed ? 1 : 0;

    public bool IsShimmering => State == "downloading";

    private static Brush? _activeFill, _pausedFill, _failedFill;

    /// <summary>The accent colour while running, amber when paused, red when failed; all faint.</summary>
    public Brush? FillBrush
    {
        get
        {
            static Brush Tint(string key, byte alpha)
            {
                var c = ((Microsoft.UI.Xaml.Media.SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color;
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
            }
            return State switch
            {
                "paused" => _pausedFill ??= Tint("SystemFillColorCautionBrush", 44),
                "failed" => _failedFill ??= Tint("SystemFillColorCriticalBrush", 40),
                _ => _activeFill ??= Tint("AccentFillColorDefaultBrush", 52),
            };
        }
    }

    public string BarText => State switch
    {
        "connecting" => "",
        "waitingToRetry" => "Retrying",
        "paused" => TotalSize is > 0 ? $"Paused {Progress:0}%" : "Paused",
        _ => TotalSize is > 0 ? $"{Progress:0}%" : Format.Bytes(Downloaded),
    };

    public string StateText => State switch
    {
        "completed" => "Done",
        "failed" => LinkExpired ? "Link expired" : "Failed",
        "queued" => "Queued",
        "paused" when AwaitingConfirmation => "Waiting for you",
        "paused" => "Paused",
        _ => StatusText,
    };

    public string StateGlyph => State switch
    {
        "completed" => "\uE73E",
        "failed" => "\uEA39",
        "queued" => "\uE823",
        "paused" when AwaitingConfirmation => "\uE897",
        "paused" => "\uE769",
        _ => "",
    };

    public bool IsPaused => State is "paused" or "queued";

    public string ProgressPercent => TotalSize is > 0 || IsCompleted ? $"{Progress:0}%" : "";

    /// <summary>Red for failed, green for done, the accent while downloading, grey otherwise.</summary>
    public Brush? StatusBrush => Microsoft.UI.Xaml.Application.Current.Resources[State switch
    {
        "failed" => "SystemFillColorCriticalBrush",
        "completed" => "SystemFillColorSuccessBrush",
        "downloading" or "connecting" => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorSecondaryBrush",
    }] as Brush;

    // ----- one-line column texts -----

    /// <summary>"12.3 MB / 100 MB" while running, the size once done.</summary>
    public string SizeText => TotalSize is > 0
        ? IsCompleted || Downloaded <= 0 ? Format.Bytes(TotalSize.Value) : $"{Format.Bytes(Downloaded)} / {Format.Bytes(TotalSize.Value)}"
        : Downloaded > 0 ? Format.Bytes(Downloaded) : "";

    public string SpeedText => State == "downloading" && Speed > 0 ? Format.Speed(Speed) : "";

    public string TimeLeftText => State == "downloading" && Speed > 0 && TotalSize is > 0
        ? Format.Duration((TotalSize.Value - Downloaded) / (double)Speed) : "";

    public string StatusText => State switch
    {
        "queued" => "Queued",
        "connecting" => "Connecting",
        "downloading" => Connections > 1 ? $"{Connections} connections" : "Downloading",
        "waitingToRetry" => "Retrying",
        "paused" when AwaitingConfirmation => "Waiting for you",
        "paused" => "Paused",
        "completed" => "Done",
        "failed" => LinkExpired ? "Link expired" : "Failed",
        _ => State,
    };

    public string AddedText
    {
        get
        {
            if (CreatedAt <= 0) return "";
            var t = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).LocalDateTime;
            return t.Date == DateTime.Today ? t.ToString("t") : t.ToString("d MMM, t");
        }
    }

    private static string? Str(JsonObject j, string key) => j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject j, string key) => j[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    public bool IsActive => State is "connecting" or "downloading" or "waitingToRetry";
    public bool IsCompleted => State == "completed";
    public bool IsFailed => State == "failed";
    public bool CanPause => IsActive || State == "queued";
    public bool CanResume => State is "paused" or "failed";

    /// <summary>A video shows its title until the file name is known.</summary>
    public string DisplayName =>
        Kind == "video" && !IsCompleted && !string.IsNullOrEmpty(Title) ? Title! :
        !string.IsNullOrEmpty(FileName) ? FileName : Url;

    public double Progress => TotalSize is > 0 ? Math.Min(100.0, Downloaded * 100.0 / TotalSize.Value) : IsCompleted ? 100 : 0;
    public bool IsIndeterminate => IsActive && TotalSize is null or 0;
    public bool ShowProgress => !IsCompleted;
    public string PercentText => TotalSize is > 0 && !IsCompleted ? $"{Progress:0}%" : "";

    public string Category => Kind == "video" ? "Video" : Categories.Of(FileName);
    public string Glyph => Categories.Glyph(Category);

    /// <summary>"12.3 MB of 100 MB · 2.1 MB/s · 1 min left", or the state.</summary>
    public string Detail
    {
        get
        {
            var size = TotalSize is > 0
                ? IsCompleted ? Format.Bytes(TotalSize.Value) : $"{Format.Bytes(Downloaded)} of {Format.Bytes(TotalSize.Value)}"
                : Downloaded > 0 ? Format.Bytes(Downloaded) : "";
            string status = State switch
            {
                "queued" => "Queued",
                "connecting" => "Connecting…",
                "downloading" => Speed > 0
                    ? $"{Format.Speed(Speed)}{Eta()}{(Connections > 1 ? $" · {Connections} connections" : "")}"
                    : "Downloading",
                "waitingToRetry" => "Connection lost, retrying…",
                "paused" when AwaitingConfirmation => "Waiting for you to confirm",
                "paused" => Resumable || Downloaded == 0 ? "Paused" : "Paused (will restart: server cannot resume)",
                "completed" => "Done",
                "failed" => LinkExpired ? "Link expired — use Refresh address" : $"Failed: {Error}",
                _ => State,
            };
            return string.IsNullOrEmpty(size) ? status : $"{size} · {status}";
        }
    }

    private string Eta()
    {
        if (TotalSize is not > 0 || Speed <= 0) return "";
        var seconds = (TotalSize.Value - Downloaded) / (double)Speed;
        return $" · {Format.Duration(seconds)} left";
    }
}

public static class Categories
{
    public static readonly string[] All = ["Video", "Music", "Documents", "Programs", "Archives", "Other"];

    public static string Of(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "ts" or "mpg" or "mpeg" or "3gp" => "Video",
            "mp3" or "m4a" or "aac" or "flac" or "wav" or "ogg" or "opus" or "wma" => "Music",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "rtf" or "odt" or "ods" or "odp" or "epub" or "csv" or "md" => "Documents",
            "exe" or "msi" or "msix" or "appx" or "apk" or "dmg" or "pkg" or "deb" or "rpm" or "appimage" => "Programs",
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "zst" or "iso" or "tgz" => "Archives",
            _ => "Other",
        };
    }

    /// <summary>Segoe Fluent Icons glyphs.</summary>
    public static string Glyph(string category) => category switch
    {
        "Video" => "",
        "Music" => "",
        "Documents" => "",
        "Programs" => "",
        "Archives" => "",
        _ => "",
    };
}

public static class Format
{
    public static string Bytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{n} B" : $"{v:0.#} {units[i]}";
    }

    public static string Speed(long bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "";
        if (seconds < 60) return $"{Math.Ceiling(seconds):0} s";
        if (seconds < 3600) return $"{Math.Ceiling(seconds / 60):0} min";
        var h = Math.Floor(seconds / 3600);
        return $"{h:0} h {Math.Ceiling((seconds - h * 3600) / 60):0} min";
    }
}

/// <summary>Sites whose pages are videos for yt-dlp rather than files.</summary>
public static class VideoSites
{
    private static readonly string[] Hosts =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "dailymotion.com", "facebook.com",
        "fb.watch", "instagram.com", "x.com", "twitter.com", "reddit.com", "tiktok.com", "twitch.tv",
    ];

    public static bool IsVideoPage(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (!Hosts.Any(h => host == h || host.EndsWith("." + h))) return false;
        // A link to a channel or the home page is not one video.
        return uri.AbsolutePath.Length > 1;
    }

    public static bool IsStream(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));

    /// <summary>A URL to a file with an extension the clipboard watch offers to download.</summary>
    public static bool LooksLikeFile(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return false;
        var name = Path.GetFileName(uri.AbsolutePath);
        return Path.HasExtension(name) && Categories.Of(name) != "Other";
    }
}
