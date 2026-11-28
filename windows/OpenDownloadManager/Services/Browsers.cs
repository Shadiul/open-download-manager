using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenDownloadManager.Services;

/// <summary>A Chromium browser installed for this user, and whether our extension is in it.</summary>
public sealed record Browser(
    string Id,
    string Name,
    string Exe,
    string ExtensionsPage,
    string UserData,
    bool ProfileIsUserData,
    string Build)
{
    /// <summary>The unpacked extension this browser loads, kept in the app's data folder.</summary>
    public string ExtensionFolder => BundledExtension.InstalledFolder(Build);

    /// <summary>
    /// Whether any of the browser's profiles has the extension, from the profile's own preferences.
    /// The browser keeps them up to date while it runs, so this notices an install within seconds.
    /// </summary>
    public bool HasExtension()
    {
        try
        {
            var profiles = ProfileIsUserData
                ? [UserData]
                : Directory.Exists(UserData)
                    ? Directory.GetDirectories(UserData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal))
                    : [];
            foreach (var profile in profiles)
                foreach (var file in new[] { "Secure Preferences", "Preferences" })
                    if (ListsExtension(Path.Combine(profile, file))) return true;
        }
        catch
        {
            // Unreadable preferences: treat as not added; the user can still add it.
        }
        return false;
    }

    private static bool ListsExtension(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings)) return false;
        if (!settings.TryGetProperty(BundledExtension.Id, out var entry)) return false;
        // A removed unpacked extension can linger with state 0 for a while; only count it when its
        // files are the ones we installed and it is not disabled by the user.
        if (entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Number && state.GetInt32() == 0
            && entry.TryGetProperty("disable_reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Number && reasons.GetInt32() != 0)
            return false;
        return true;
    }

    /// <summary>Opens the browser on its extensions page, with the extension's folder ready in Explorer.</summary>
    public void StartInstall()
    {
        BundledExtension.Sync();
        // A test copy (ODM_PROFILE) must not take the browsers' connection from the installed app.
        if (AppPaths.Profile is null) NativeHostRegistration.Register();
        try { Process.Start(new ProcessStartInfo(Exe, ExtensionsPage) { UseShellExecute = false }); }
        catch { /* the guide still shows the page's address */ }
        ShowFolder();
    }

    /// <summary>Explorer with the extension folder selected, ready to drag onto the extensions page.</summary>
    public void ShowFolder()
    {
        if (Directory.Exists(ExtensionFolder)) Process.Start("explorer.exe", $"/select,\"{ExtensionFolder}\"");
    }
}

/// <summary>Finds the Chromium browsers the extension works in.</summary>
public static class Browsers
{
    private static string Local(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. parts]);

    private static string Roaming(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), .. parts]);

    // Chrome gets the Chrome build (no YouTube, as the Chrome Web Store requires); the others the full one.
    private static readonly (string Id, string Name, string ExeName, string Page, Func<string> UserData, bool ProfileIsUserData, string Build)[] Known =
    [
        ("edge", "Microsoft Edge", "msedge.exe", "edge://extensions", () => Local("Microsoft", "Edge", "User Data"), false, "edge"),
        ("chrome", "Google Chrome", "chrome.exe", "chrome://extensions", () => Local("Google", "Chrome", "User Data"), false, "chrome"),
        ("brave", "Brave", "brave.exe", "brave://extensions", () => Local("BraveSoftware", "Brave-Browser", "User Data"), false, "edge"),
        ("vivaldi", "Vivaldi", "vivaldi.exe", "vivaldi://extensions", () => Local("Vivaldi", "User Data"), false, "edge"),
        ("opera", "Opera", "opera.exe", "opera://extensions", () => Roaming("Opera Software", "Opera Stable"), true, "edge"),
    ];

    /// <summary>The installed ones, in a fixed order (Edge first: it comes with Windows).</summary>
    public static List<Browser> Installed()
    {
        var found = new List<Browser>();
        foreach (var k in Known)
        {
            var exe = FindExe(k.ExeName, k.Id);
            if (exe is null) continue;
            found.Add(new Browser(k.Id, k.Name, exe, k.Page, k.UserData(), k.ProfileIsUserData, k.Build));
        }
        return found;
    }

    /// <summary>App Paths first (where installers register), then the usual install folders.</summary>
    private static string? FindExe(string exeName, string id)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] guesses = id switch
        {
            "edge" => [Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")],
            "chrome" => [Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Local(@"Google\Chrome\Application\chrome.exe")],
            "brave" => [Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Local(@"BraveSoftware\Brave-Browser\Application\brave.exe")],
            "vivaldi" => [Local(@"Vivaldi\Application\vivaldi.exe"), Path.Combine(pf, @"Vivaldi\Application\vivaldi.exe")],
            "opera" => [Local(@"Programs\Opera\opera.exe"), Local(@"Programs\Opera\launcher.exe")],
            _ => [],
        };
        return guesses.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// The extension ships inside the app (extension\edge and extension\chrome) and is copied to the
/// data folder, which browsers load it from. Each app update refreshes that copy, and the browser
/// picks up the new files the next time it starts.
/// </summary>
public static class BundledExtension
{
    /// <summary>Fixed by the <c>key</c> in the extension's manifest.json.</summary>
    public const string Id = "ohammiojomdnbnfhigkofgahnghdmolg";

    private static string Shipped(string build) => Path.Combine(AppPaths.AppDir, "extension", build);

    public static string InstalledFolder(string build) => Path.Combine(AppPaths.DataDir, "extension", build);

    public static bool IsBundled => Directory.Exists(Shipped("edge")) || Directory.Exists(Shipped("chrome"));

    /// <summary>Brings the data-folder copy up to date with the shipped one. Cheap when nothing changed.</summary>
    public static void Sync()
    {
        foreach (var build in new[] { "edge", "chrome" })
        {
            var from = Shipped(build);
            var to = InstalledFolder(build);
            if (!Directory.Exists(from)) continue;
            if (File.Exists(Path.Combine(to, "manifest.json"))
                && File.ReadAllText(Path.Combine(to, "manifest.json")) == File.ReadAllText(Path.Combine(from, "manifest.json"))
                && SameFiles(from, to))
                continue;
            // Replace file by file: the browser may hold the folder open.
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            foreach (var stale in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
                if (!File.Exists(Path.Combine(from, Path.GetRelativePath(to, stale)))) File.Delete(stale);
        }
    }

    private static bool SameFiles(string a, string b)
    {
        var left = Directory.GetFiles(a, "*", SearchOption.AllDirectories);
        if (left.Length != Directory.GetFiles(b, "*", SearchOption.AllDirectories).Length) return false;
        foreach (var file in left)
        {
            var other = Path.Combine(b, Path.GetRelativePath(a, file));
            if (!File.Exists(other) || new FileInfo(other).Length != new FileInfo(file).Length
                || File.GetLastWriteTimeUtc(other) < File.GetLastWriteTimeUtc(file))
                return false;
        }
        return true;
    }
}
