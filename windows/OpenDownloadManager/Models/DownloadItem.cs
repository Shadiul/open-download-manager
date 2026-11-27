using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

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
    [ObservableProperty] public partial bool AwaitingConfirmation { get; set; }

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
