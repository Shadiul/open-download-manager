using System.Text.Json;

namespace OpenDownloadManager.Services;

/// <summary>
/// Each download's speed against its progress, recorded all the time (not only while its progress
/// window is open) and saved with the app's data, so the graph shows the whole download when the
/// window opens late, after a pause, or after a restart.
/// </summary>
public static class SpeedHistory
{
    /// <summary>At most this many points per download: one per 1/600 of the file.</summary>
    private const double Step = 1.0 / 600;

    private static readonly Dictionary<ulong, List<double[]>> Points = Load();
    private static readonly object Gate = new();
    private static DateTime _savedAt = DateTime.MinValue;
    private static bool _dirty;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "speed-history.json");

    /// <summary>Notes the speed at this point of the download (fraction 0 to 1).</summary>
    public static void Record(ulong id, double at, double speed)
    {
        if (speed <= 0 || at is < 0 or > 1) return;
        lock (Gate)
        {
            if (!Points.TryGetValue(id, out var list)) Points[id] = list = [];
            if (list.Count > 0 && at + 0.01 < list[^1][0]) list.Clear(); // it started over
            if (list.Count > 0 && at - list[^1][0] < Step) return;
            list.Add([Math.Round(at, 5), Math.Round(speed)]);
            _dirty = true;
        }
        if (DateTime.UtcNow - _savedAt > TimeSpan.FromSeconds(10)) Save();
    }

    /// <summary>The points so far, oldest first.</summary>
    public static List<(double At, double Speed)> Get(ulong id)
    {
        lock (Gate)
            return Points.TryGetValue(id, out var list) ? list.Select(p => (p[0], p[1])).ToList() : [];
    }

    public static void Forget(ulong id)
    {
        lock (Gate) _dirty |= Points.Remove(id);
    }

    /// <summary>Keeps only downloads still in the list.</summary>
    public static void Keep(IEnumerable<ulong> ids)
    {
        var keep = ids.ToHashSet();
        lock (Gate)
            foreach (var id in Points.Keys.Where(k => !keep.Contains(k)).ToList())
                _dirty |= Points.Remove(id);
    }

    public static void Save()
    {
        string json;
        lock (Gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(Points);
            _dirty = false;
            _savedAt = DateTime.UtcNow;
        }
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception e)
        {
            Log.Error("SpeedHistory.Save", e);
        }
    }

    private static Dictionary<ulong, List<double[]>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<ulong, List<double[]>>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e)
        {
            Log.Error("SpeedHistory.Load", e);
        }
        return [];
    }
}
