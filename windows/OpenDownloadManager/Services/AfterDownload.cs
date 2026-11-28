using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using OpenDownloadManager.Core;
using OpenDownloadManager.Models;

namespace OpenDownloadManager.Services;

/// <summary>
/// What happens after downloads: to each finished file (open it, show it, extract an archive),
/// and to the computer once the whole queue is done (sleep, shut down and so on).
/// </summary>
public static class AfterDownload
{
    /// <summary>
    /// The queue actions: the engine's name, the menu label, a short form for the status bar, and
    /// what the countdown says.
    /// </summary>
    public static readonly (string Value, string Label, string Short, string Glyph, string Countdown)[] QueueActions =
    [
        ("nothing", "Do nothing", "nothing", "", ""),
        ("sleep", "Sleep", "sleep", "", "The computer will go to sleep in {0} seconds."),
        ("hibernate", "Hibernate", "hibernate", "", "The computer will hibernate in {0} seconds."),
        ("shutdown", "Shut down", "shut down", "", "The computer will shut down in {0} seconds."),
        ("restart", "Restart", "restart", "", "The computer will restart in {0} seconds."),
        ("signOut", "Sign out", "sign out", "", "You will be signed out of Windows in {0} seconds."),
        ("lock", "Lock the computer", "lock", "", "The computer will lock in {0} seconds."),
        ("quit", "Close Open Download Manager", "close the app", "", "Open Download Manager will close in {0} seconds."),
    ];

    public static string Label(string value) => QueueActions.FirstOrDefault(a => a.Value == value).Label ?? "Do nothing";

    /// <summary>Files that run code when opened; "open when done" never starts these by itself.</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".com", ".scr", ".pif",
        ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".lnk", ".reg", ".jar",
    };

    private static readonly string[] ArchiveEndings = [".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.xz", ".txz", ".tar.bz2", ".tbz2", ".tar.zst"];

    public static bool IsArchive(string path) => ArchiveEndings.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the per-file actions the user turned on, for a download that just finished.</summary>
    public static void OnFileFinished(DownloadItem item, AppSettings settings)
    {
        var path = item.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (settings.AfterExtract && IsArchive(path))
        {
            var delete = settings.AfterExtractDelete;
            _ = Task.Run(() => Extract(path, delete));
            return;
        }
        if (settings.AfterOpen && !Runnable.Contains(Path.GetExtension(path))) Shell.Open(path);
        else if (settings.AfterShowInFolder) Shell.ShowInFolder(path, item.SaveDir);
    }

    /// <summary>
    /// Unpacks into a folder named after the archive, next to it, with Windows' own tar (which reads
    /// zip, 7z, rar and tar archives). The archive goes to the Recycle Bin afterwards if asked.
    /// </summary>
    /// <summary>
    /// An archive's extraction started (target null, ok false), finished (target set, ok true) or
    /// failed (ok false). Raised on a background thread.
    /// </summary>
    public static event Action<string, string?, bool>? ExtractChanged;

    public static bool Extract(string archive, bool deleteAfter)
    {
        ExtractChanged?.Invoke(archive, null, false);
        var ok = ExtractCore(archive, deleteAfter, out var folder);
        ExtractChanged?.Invoke(archive, folder ?? "", ok);
        return ok;
    }

    private static bool ExtractCore(string archive, bool deleteAfter, out string? folder)
    {
        folder = null;
        try
        {
            var dir = Path.GetDirectoryName(archive)!;
            var name = Path.GetFileName(archive);
            foreach (var e in ArchiveEndings.OrderByDescending(e => e.Length))
                if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) { name = name[..^e.Length]; break; }
            var target = Path.Combine(dir, name);
            for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = Path.Combine(dir, $"{name} ({n})");
            Directory.CreateDirectory(target);
            var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            using var p = Process.Start(new ProcessStartInfo(tar)
            {
                ArgumentList = { "-xf", archive, "-C", target },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Log.Error("extract", new Exception($"{archive}: tar exited {p.ExitCode}: {error}"));
                if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
                return false;
            }
            if (deleteAfter) Recycle(archive);
            folder = target;
            Shell.ShowInFolder(null, target);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("extract", e);
            return false;
        }
    }

    /// <summary>Sends a file to the Recycle Bin, so a wrong guess can be undone.</summary>
    private static void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        SHFileOperation(ref op);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Saves the queue action in the engine's settings (keeping every other setting).</summary>
    public static async Task SetQueueAction(CoreClient core, string value)
    {
        var reply = await core.CallAsync(new { type = "getSettings" });
        if (reply["settings"] is not JsonObject settings) return;
        settings["whenDone"] = value;
        await core.CallAsync(new { type = "setSettings", settings });
        QueueActionChanged?.Invoke(value);
    }

    public static string QueueAction(CoreClient core) =>
        (core.Call(new { type = "getSettings" })["settings"] as JsonObject)?["whenDone"]?.GetValue<string>() ?? "nothing";

    /// <summary>Raised when the queue action is changed from anywhere (Settings, status bar, tray).</summary>
    public static event Action<string>? QueueActionChanged;

    public static void RaiseQueueActionChanged(string value) => QueueActionChanged?.Invoke(value);

    /// <summary>A menu of the queue actions with the current one checked.</summary>
    public static void FillMenu(IList<MenuFlyoutItemBase> items, CoreClient core, string current)
    {
        items.Clear();
        foreach (var (value, label, _, glyph, _) in QueueActions)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "whenDone", IsChecked = value == current, Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) => await SetQueueAction(core, value);
            items.Add(item);
            if (value == "nothing") items.Add(new MenuFlyoutSeparator());
        }
    }

    /// <summary>Carries out a queue action (after its countdown).</summary>
    public static void Perform(string value, Action quitApp)
    {
        switch (value)
        {
            case "sleep": Power.Sleep(); break;
            case "hibernate": Power.Hibernate(); break;
            case "shutdown": Power.Shutdown(); break;
            case "restart": Power.Restart(); break;
            case "signOut": Power.SignOut(); break;
            case "lock": Power.Lock(); break;
            case "quit": quitApp(); break;
        }
    }
}
