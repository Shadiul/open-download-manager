using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.ViewModels;

/// <summary>The download list, kept in step with the core's events, filtered, searched and sorted.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<ulong, DownloadItem> _items = [];

    public CoreClient Core { get; }
    public AppSettings AppSettings { get; }

    /// <summary>The items the current filters show, in the current sort order.</summary>
    public ObservableCollection<DownloadItem> Visible { get; } = [];

    /// <summary>The navigation pane's choice: all, active, completed, or a category.</summary>
    [ObservableProperty] public partial string Filter { get; set; } = "all";
    /// <summary>any, downloading, queued, paused, completed or failed.</summary>
    [ObservableProperty] public partial string StatusFilter { get; set; } = "any";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    /// <summary>queue, name, size, progress, speed, status or added.</summary>
    [ObservableProperty] public partial string SortBy { get; set; } = "queue";
    [ObservableProperty] public partial bool SortDescending { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string EmptyTitle { get; set; } = "No downloads here yet";
    [ObservableProperty] public partial string? ErrorText { get; set; }

    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;
    /// <summary>A download began connecting (from queued or paused).</summary>
    public event Action<DownloadItem>? Started;
    public event Action<string>? QueueFinished;
    /// <summary>A browser download waits for the new-download window.</summary>
    public event Action<DownloadItem>? NeedsConfirmation;

    /// <summary>Downloads still waiting from an earlier session, to ask about again.</summary>
    public IEnumerable<DownloadItem> AwaitingConfirmation => _items.Values.Where(i => i.AwaitingConfirmation);

    public MainViewModel(CoreClient core, AppSettings appSettings)
    {
        Core = core;
        AppSettings = appSettings;
        SortBy = appSettings.SortBy;
        SortDescending = appSettings.SortDescending;
        Core.EventReceived += OnEvent;
    }

    public DownloadItem? Find(ulong id) => _items.GetValueOrDefault(id);

    public IEnumerable<DownloadItem> All => _items.Values;

    public void Load()
    {
        var reply = Core.Call(new { type = "list" });
        if (reply["jobs"] is JsonArray jobs)
        {
            foreach (var job in jobs.OfType<JsonObject>())
            {
                var item = DownloadItem.From(job);
                _items[item.Id] = item;
            }
        }
        Refilter();
    }

    partial void OnFilterChanged(string value) => Refilter();
    partial void OnStatusFilterChanged(string value) => Refilter();
    partial void OnSearchTextChanged(string value) => Refilter();
    partial void OnSortByChanged(string value) => SaveSort();
    partial void OnSortDescendingChanged(bool value) => SaveSort();

    /// <summary>Sorts by a column; the same column again flips the direction.</summary>
    public void SortOn(string column)
    {
        if (SortBy == column) SortDescending = !SortDescending;
        else
        {
            SortBy = column;
            // Sizes, progress, speed and dates read best largest or newest first.
            SortDescending = column is "size" or "progress" or "speed" or "added";
        }
    }

    private void SaveSort()
    {
        AppSettings.SortBy = SortBy;
        AppSettings.SortDescending = SortDescending;
        try { AppSettings.Save(); } catch { }
        Refilter();
    }

    private void OnEvent(JsonObject ev)
    {
        switch (ev["type"]?.GetValue<string>())
        {
            case "jobAdded" when ev["job"] is JsonObject job:
                {
                    var item = DownloadItem.From(job);
                    _items[item.Id] = item;
                    Place(item);
                    if (item.AwaitingConfirmation) NeedsConfirmation?.Invoke(item);
                    break;
                }
            case "jobUpdated" when ev["job"] is JsonObject job:
                {
                    var id = (ulong)(job["id"]?.GetValue<long>() ?? 0);
                    if (!_items.TryGetValue(id, out var item))
                    {
                        item = DownloadItem.From(job);
                        _items[id] = item;
                        Place(item);
                        break;
                    }
                    var (wasState, wasPosition, wasName) = (item.State, item.Position, item.DisplayName);
                    item.Update(job);
                    // Only changes that move a row re-place it, so rows don't jump on every progress tick.
                    if (wasPosition != item.Position || wasState != item.State || wasName != item.DisplayName
                        || Matches(item) != Visible.Contains(item))
                        Place(item);
                    if (wasState is "queued" or "paused" && item.State == "connecting") Started?.Invoke(item);
                    if (wasState != "completed" && item.State == "completed") Completed?.Invoke(item);
                    if (wasState != "failed" && item.State == "failed") Failed?.Invoke(item);
                    break;
                }
            case "jobRemoved":
                {
                    var id = (ulong)(ev["id"]?.GetValue<long>() ?? 0);
                    if (_items.Remove(id, out var item)) Visible.Remove(item);
                    break;
                }
            case "queueFinished":
                QueueFinished?.Invoke(ev["whenDone"]?.GetValue<string>() ?? "nothing");
                break;
            case "activity":
                Power.KeepAwake((ev["active"]?.GetValue<int>() ?? 0) > 0);
                break;
            case "error":
                ErrorText = ev["message"]?.GetValue<string>();
                break;
        }
        UpdateStatus();
    }

    private bool Matches(DownloadItem item)
    {
        var nav = Filter switch
        {
            "all" => true,
            "active" => !item.IsCompleted,
            "completed" => item.IsCompleted,
            _ => item.Category == Filter,
        };
        if (!nav) return false;
        var status = StatusFilter switch
        {
            "downloading" => item.IsActive,
            "queued" => item.State == "queued",
            "paused" => item.State == "paused",
            "completed" => item.IsCompleted,
            "failed" => item.IsFailed,
            _ => true,
        };
        if (!status) return false;
        var q = SearchText.Trim();
        return q.Length == 0
            || item.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || item.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private int Compare(DownloadItem a, DownloadItem b)
    {
        var c = SortBy switch
        {
            "name" => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
            "size" => (a.TotalSize ?? a.Downloaded).CompareTo(b.TotalSize ?? b.Downloaded),
            "progress" => a.Progress.CompareTo(b.Progress),
            "speed" => a.Speed.CompareTo(b.Speed),
            "status" => string.Compare(a.StatusText, b.StatusText, StringComparison.CurrentCulture),
            "added" => a.CreatedAt.CompareTo(b.CreatedAt),
            _ => a.Position.CompareTo(b.Position),
        };
        if (SortDescending) c = -c;
        return c != 0 ? c : a.Position.CompareTo(b.Position);
    }

    private void Refilter()
    {
        var keep = _items.Values.Where(Matches).ToList();
        keep.Sort(Compare);
        Visible.Clear();
        foreach (var item in keep) Visible.Add(item);
        UpdateStatus();
    }

    /// <summary>Puts one item where it belongs in the visible list, or takes it out.</summary>
    private void Place(DownloadItem item)
    {
        var at = Visible.IndexOf(item);
        if (!Matches(item))
        {
            if (at >= 0) Visible.RemoveAt(at);
            return;
        }
        var index = 0;
        while (index < Visible.Count && (Visible[index] == item || Compare(Visible[index], item) <= 0)) index++;
        if (at >= 0 && index > at) index--;
        if (at == index) return;
        if (at >= 0) Visible.Move(at, index);
        else Visible.Insert(index, item);
    }

    private void UpdateStatus()
    {
        IsEmpty = Visible.Count == 0;
        EmptyTitle = _items.Count == 0 ? "No downloads here yet" : "Nothing matches these filters";
        var active = _items.Values.Where(i => i.IsActive).ToList();
        var queued = _items.Values.Count(i => i.State == "queued");
        var speed = active.Sum(i => i.Speed);
        var shown = Visible.Count == _items.Count ? $"{_items.Count} downloads" : $"{Visible.Count} of {_items.Count} shown";
        StatusText = shown + " · " + (active.Count == 0
            ? queued > 0 ? $"{queued} queued" : "nothing running"
            : $"{active.Count} downloading at {Format.Speed(speed)}" + (queued > 0 ? $", {queued} queued" : ""));
        App.Current?.UpdateTrayTooltip(active.Count == 0
            ? AppPaths.AppName
            : $"{AppPaths.AppName}\n{active.Count} downloading · {Format.Speed(speed)}");
    }
}
