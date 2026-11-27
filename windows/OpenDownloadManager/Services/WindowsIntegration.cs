using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenDownloadManager.Services;

/// <summary>Registers odm-host.exe as the native messaging host for Chrome and Edge (current user).</summary>
public static class NativeHostRegistration
{
    public const string HostName = "org.opendownloadmanager.host";

    /// <summary>
    /// Extensions allowed to start the host: the unpacked development build (its ID is fixed by
    /// the <c>key</c> in manifest.json). Store IDs are added here once the listings exist.
    /// </summary>
    public static readonly string[] AllowedOrigins =
    [
        "chrome-extension://ohammiojomdnbnfhigkofgahnghdmolg/",
    ];

    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
    ];

    public static string ManifestPath => Path.Combine(AppPaths.DataDir, HostName + ".json");

    /// <summary>Writes the manifest and the registry keys. Safe to call on every start.</summary>
    public static void Register()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var manifest = new
        {
            name = HostName,
            description = AppPaths.AppName,
            path = AppPaths.HostExe,
            type = "stdio",
            allowed_origins = AllowedOrigins,
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var key in BrowserKeys)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue(null, ManifestPath);
        }
    }

    public static bool IsRegistered()
    {
        foreach (var key in BrowserKeys)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            if (k?.GetValue(null) as string != ManifestPath) return false;
        }
        return File.Exists(ManifestPath) && File.Exists(AppPaths.HostExe);
    }
}

/// <summary>Starts the app with Windows, in the tray.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenDownloadManager";

    public static void Apply(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{AppPaths.AppExe}\" --background");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Keeps the computer awake while downloads run, and carries out sleep or shutdown.</summary>
public static class Power
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    /// <summary>Call from the UI thread, which lives as long as the app.</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Shutdown() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
}

/// <summary>
/// Tags finished files with Mark of the Web through IAttachmentExecute, which also runs the
/// installed antivirus scan, so Windows warns before opening a downloaded program just as it
/// does for browser downloads.
/// </summary>
public static class MarkOfTheWeb
{
    private static readonly Guid ClientGuid = new("5b0a6a4f-0cf1-4c39-9f4b-3a8e3e1b2f60");

    public static void Apply(string path, string sourceUrl, string? referrer)
    {
        try
        {
            var ae = (IAttachmentExecute)new AttachmentServices();
            try
            {
                var guid = ClientGuid;
                ae.SetClientGuid(ref guid);
                ae.SetLocalPath(path);
                ae.SetSource(sourceUrl);
                if (!string.IsNullOrEmpty(referrer)) ae.SetReferrer(referrer);
                ae.Save();
            }
            finally
            {
                Marshal.ReleaseComObject(ae);
            }
        }
        catch
        {
            WriteZoneIdentifier(path, sourceUrl, referrer);
        }
    }

    private static void WriteZoneIdentifier(string path, string sourceUrl, string? referrer)
    {
        try
        {
            var text = "[ZoneTransfer]\r\nZoneId=3\r\n"
                + (string.IsNullOrEmpty(referrer) ? "" : $"ReferrerUrl={referrer}\r\n")
                + $"HostUrl={sourceUrl}\r\n";
            File.WriteAllText(path + ":Zone.Identifier", text);
        }
        catch
        {
            // File systems without alternate data streams (FAT, network shares) cannot be marked.
        }
    }

    [ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")]
    private class AttachmentServices;

    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetClientGuid(ref Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
        void Save();
        [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
        [PreserveSig] int SaveWithUI(IntPtr hwnd);
        [PreserveSig] int ClearClientState();
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* no app for this file type: Windows shows nothing; nothing to do */ }
    }

    /// <summary>Opens Explorer with the file selected, or the folder if the file is gone.</summary>
    public static void ShowInFolder(string? file, string folder)
    {
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        else if (Directory.Exists(folder))
            Process.Start("explorer.exe", $"\"{folder}\"");
    }
}
