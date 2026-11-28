using System.Runtime.InteropServices;

namespace OpenDownloadManager.Services;

/// <summary>
/// The tray icon's right-click menu, as a native Windows menu. Windows measures it, so labels never
/// clip at any scaling or in any language, submenus open beside it, and it looks like the other tray
/// menus. (H.NotifyIcon's own menu hosted a WinUI MenuFlyout in a second window sized before the
/// items had their template, so the window came out too narrow and cut the labels and the submenu.)
/// </summary>
public static class TrayMenu
{
    public abstract record Entry;
    public sealed record Command(string Text, Action Run, bool IsDefault = false, bool Enabled = true) : Entry;
    public sealed record Radio(string Text, bool Checked, Action Run) : Entry;
    public sealed record Separator : Entry;
    public sealed record Submenu(string Text, IReadOnlyList<Entry> Items) : Entry;

    /// <summary>Shows the menu at a screen point (physical pixels) and runs the chosen entry.</summary>
    /// <param name="owner">A window of this app; it takes the foreground so the menu closes on an outside click.</param>
    public static void Show(IntPtr owner, int x, int y, IReadOnlyList<Entry> entries, bool dark)
    {
        var actions = new List<Action>();
        var menu = Build(entries, actions);
        try
        {
            // Menus follow the app's light or dark choice (uxtheme's undocumented but stable app mode).
            try
            {
                SetPreferredAppMode(dark ? 2 : 3); // ForceDark : ForceLight
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException) { }

            SetForegroundWindow(owner);
            var flags = TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON
                | (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN) | TPM_BOTTOMALIGN;
            var chosen = TrackPopupMenuEx(menu, flags, x, y, owner, IntPtr.Zero);
            // Lets the menu close properly when the user clicks elsewhere (KB135788).
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= actions.Count) actions[chosen - 1]();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr Build(IReadOnlyList<Entry> entries, List<Action> actions)
    {
        var menu = CreatePopupMenu();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case Separator:
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    break;
                case Submenu sub:
                    AppendMenu(menu, MF_POPUP, (UIntPtr)(ulong)Build(sub.Items, actions), sub.Text);
                    break;
                case Command c:
                    actions.Add(c.Run);
                    AppendMenu(menu, MF_STRING | (c.Enabled ? 0 : MF_GRAYED), (UIntPtr)(uint)actions.Count, c.Text);
                    if (c.IsDefault) SetMenuDefaultItem(menu, (uint)actions.Count, 0);
                    break;
                case Radio r:
                    actions.Add(r.Run);
                    var id = (uint)actions.Count;
                    AppendMenu(menu, MF_STRING, (UIntPtr)id, r.Text);
                    var info = new MENUITEMINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                        fMask = MIIM_FTYPE | MIIM_STATE,
                        fType = MFT_RADIOCHECK,
                        fState = r.Checked ? MFS_CHECKED : 0,
                    };
                    SetMenuItemInfo(menu, id, false, ref info);
                    break;
            }
        }
        return menu;
    }

    private const uint MF_STRING = 0, MF_GRAYED = 1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const uint MIIM_STATE = 1, MIIM_FTYPE = 0x100, MFT_RADIOCHECK = 0x200, MFS_CHECKED = 8;
    private const uint TPM_LEFTALIGN = 0, TPM_RIGHTALIGN = 8, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int SM_MENUDROPALIGNMENT = 40;
    private const uint WM_NULL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
