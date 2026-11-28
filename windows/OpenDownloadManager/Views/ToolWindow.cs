using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.Views;

/// <summary>
/// A small fixed-width window whose height follows its content: Mica, the app's icon and theme,
/// centred a little above the middle of the screen.
/// </summary>
public abstract class ToolWindow : Window
{
    protected ToolWindow(string title, int width)
    {
        Title = title;
        SystemBackdrop = Appearance.Backdrop();
        AppWindow.SetIcon(AppPaths.IconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 400));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
        }
    }

    /// <summary>Call once the content is set.</summary>
    protected void Ready()
    {
        ViewSettings.Current.Track(this);
        Appearance.Track(this);
        if (Content is FrameworkElement root) root.Loaded += (_, _) => { FitHeight(); CenterOnScreen(); };
        CenterOnScreen();
    }

    protected void FitHeight()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        var scale = root.XamlRoot.RasterizationScale;
        var width = AppWindow.ClientSize.Width / scale;
        root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var height = (int)Math.Ceiling(root.DesiredSize.Height * scale);
        if (height > 0 && height != AppWindow.ClientSize.Height)
            AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(AppWindow.ClientSize.Width, height));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 3));
    }

    public void ShowOnTop()
    {
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
