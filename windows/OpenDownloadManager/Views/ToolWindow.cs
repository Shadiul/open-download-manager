using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenDownloadManager.Services;

namespace OpenDownloadManager.Views;

/// <summary>
/// A small fixed-width window whose height follows its content: Mica, the app's icon and theme,
/// centred a little above the middle of the screen. Its title bar is drawn here so it can show the
/// one-colour glyph that follows light and dark; the taskbar and Alt+Tab keep the colour app icon.
/// </summary>
public abstract class ToolWindow : Window
{
    private readonly Image _glyph = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };

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
        // The title bar: glyph and title over the window's own content; Windows keeps the caption buttons.
        if (Content is UIElement body)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Height = 32,
                Padding = new Thickness(12, 0, 0, 0),
                Children = { _glyph, new TextBlock { Text = Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
            };
            var frame = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
            Content = null;
            Grid.SetRow((FrameworkElement)body, 1);
            frame.Children.Add(bar);
            frame.Children.Add(body);
            Content = frame;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(bar);
            ShowGlyph();
            ThemeColors.Changed += ShowGlyph;
            Closed += (_, _) => ThemeColors.Changed -= ShowGlyph;
        }
        ViewSettings.Current.Track(this);
        Appearance.Track(this);
        if (Content is FrameworkElement root) root.Loaded += (_, _) => { FitHeight(); CenterOnScreen(); };
        CenterOnScreen();
    }

    /// <summary>The white glyph on a dark window, the dark one on a light window.</summary>
    private void ShowGlyph() =>
        _glyph.Source = new BitmapImage(new Uri(AppPaths.TrayIconPath(lightTaskbar: !ThemeColors.IsDark)));

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
