using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace OpenDownloadManager;

/// <summary>
/// One app per user: a second launch (from the Start menu, the native host or a toast) hands its
/// activation to the running instance and exits.
/// </summary>
public static class Program
{
    private static readonly string InstanceKey =
        "OpenDownloadManager-main" + (Services.AppPaths.Profile is { } p ? "-" + p : "");

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (Redirected()) return 0;
        if (AnotherCopyRunning()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool Redirected()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            // "--quit" closes a running copy (the installer uses it); with none running there is nothing to do.
            if (Environment.GetCommandLineArgs().Contains("--quit")) return true;
            main.Activated += (_, e) => App.Current?.OnRedirectedActivation(e);
            return false;
        }

        // Wait on a separate thread: blocking this STA thread on the redirect call can deadlock.
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            main.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        return true;
    }

    /// <summary>Held for the app's lifetime: one copy per user (and profile), wherever it is installed.</summary>
    private static Mutex? _oneCopy;

    /// <summary>
    /// Windows hands a second launch over only to a copy started from the same file. A copy from
    /// another folder (an installed one next to a portable or development one) would run beside it,
    /// sharing the download list's database but not the browser connection, so downloads would show
    /// in one window and not the other. Say which copy is running, and leave it be.
    /// </summary>
    private static bool AnotherCopyRunning()
    {
        _oneCopy = new Mutex(true, @"Local\" + InstanceKey + "-" + Environment.UserName, out var mine);
        if (mine) return false;
        if (Environment.GetCommandLineArgs().Contains("--quit") || Environment.GetCommandLineArgs().Contains("--background"))
            return true;
        var self = Environment.ProcessPath;
        var other = System.Diagnostics.Process.GetProcessesByName("OpenDownloadManager")
            .Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
            .FirstOrDefault(path => path is not null && !string.Equals(path, self, StringComparison.OrdinalIgnoreCase));
        MessageBoxW(IntPtr.Zero,
            "Open Download Manager is already running" + (other is null ? "." : $" from:\n{other}") +
            "\n\nUse that copy, or quit it from its icon in the notification area before starting this one.",
            "Open Download Manager", 0x40 /* MB_ICONINFORMATION */);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);
}
