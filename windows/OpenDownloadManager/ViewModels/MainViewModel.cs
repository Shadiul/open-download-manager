using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.ViewModels;

/// <summary>The download list, kept in step with the core's events.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<ulong, DownloadItem> _items = [];

    public CoreClient Core { get; }
    public AppSettings AppSettings { get; }

    /// <summary>The items the current filter shows, in queue order.</summary>
    public ObservableCollection<DownloadItem> Visible { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "all";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }

    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;
    public event Action<string>? QueueFinished;
    /// <summary>A browser download waits for the new-download window.</summary>
    public event Action<DownloadItem>? NeedsConfirmation;

    /// <summary>Downloads still waiting from an earlier session, to ask about again.</summary>
    public IEnumerable<DownloadItem> AwaitingConfirmation => _items.Values.Where(i => i.AwaitingConfirmation);

    public MainViewModel(CoreClient core, AppSettings appSettings)
    {
        Core = core;
        AppSettings = appSettings;
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
                    var (wasState, wasPosition) = (item.State, item.Position);
                    item.Update(job);
                    if (wasPosition != item.Position || Matches(item) != Visible.Contains(item)) Place(item);
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

    private bool Matches(DownloadItem item) => Filter switch
    {
        "all" => true,
        "active" => !item.IsCompleted,
        "completed" => item.IsCompleted,
        _ => item.Category == Filter,
    };

    private void Refilter()
    {
        Visible.Clear();
        foreach (var item in _items.Values.Where(Matches).OrderBy(i => i.Position))
            Visible.Add(item);
        UpdateStatus();
    }

    /// <summary>Puts one item where it belongs in the visible list, or takes it out.</summary>
    private void Place(DownloadItem item)
    {
        Visible.Remove(item);
        if (!Matches(item)) return;
        var index = 0;
        while (index < Visible.Count && Visible[index].Position < item.Position) index++;
        Visible.Insert(index, item);
    }

    private void UpdateStatus()
    {
        IsEmpty = Visible.Count == 0;
        var active = _items.Values.Where(i => i.IsActive).ToList();
        var queued = _items.Values.Count(i => i.State == "queued");
        var speed = active.Sum(i => i.Speed);
        StatusText = active.Count == 0
            ? queued > 0 ? $"{queued} queued" : "No downloads running"
            : $"{active.Count} downloading · {Format.Speed(speed)}" + (queued > 0 ? $" · {queued} queued" : "");
        App.Current?.UpdateTrayTooltip(active.Count == 0
            ? AppPaths.AppName
            : $"{AppPaths.AppName}\n{active.Count} downloading · {Format.Speed(speed)}");
    }
}
