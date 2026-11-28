using System.Runtime.InteropServices;

namespace OpenDownloadManager.Services;

/// <summary>
/// Gives the app's shortcuts (Start menu, desktop, pinned to the taskbar) the icon chosen in Settings,
/// so they match its windows. The icon inside OpenDownloadManager.exe itself can't change.
/// Only the installed app does this: a development copy leaves the user's shortcuts alone.
/// </summary>
public static class AppShortcuts
{
    public static void Sync()
    {
        if (AppPaths.IsDev) return;
        var smoke = AppPaths.AppIcon == "smoke";
        var icon = smoke ? AppPaths.IconPath : $"{AppPaths.AppExe},0";
        Task.Run(() =>
        {
            try { if (Update(icon, smoke)) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }   // SHCNE_ASSOCCHANGED: redraw icons
            catch (Exception e) { Log.Error("shortcut icons", e); }
        });
    }

    private static bool Update(string icon, bool smoke)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        };
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return false;
        dynamic shell = Activator.CreateInstance(type)!;
        var changed = false;
        try
        {
            foreach (var folder in folders.Where(Directory.Exists))
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    dynamic link = shell.CreateShortcut(path);
                    try
                    {
                        if (!string.Equals((string)link.TargetPath, AppPaths.AppExe, StringComparison.OrdinalIgnoreCase)) continue;
                        var current = (string)link.IconLocation;
                        // Back on Orbit, only shortcuts this changed are touched.
                        if (smoke ? string.Equals(current, icon + ",0", StringComparison.OrdinalIgnoreCase)
                                  : !current.Contains("app-smoke", StringComparison.OrdinalIgnoreCase)) continue;
                        link.IconLocation = smoke ? icon + ",0" : icon;
                        link.Save();
                        changed = true;
                    }
                    finally { Marshal.FinalReleaseComObject(link); }
                }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return changed;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
