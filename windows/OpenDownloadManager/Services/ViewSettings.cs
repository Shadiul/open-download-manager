using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace OpenDownloadManager.Services;

/// <summary>
/// Appearance and list layout, shared by every window and bound from the list's row template.
/// Changes are saved to the app settings right away.
/// </summary>
public sealed partial class ViewSettings : ObservableObject
{
    public static ViewSettings Current { get; } = new();

    private AppSettings? _store;
    private double _listWidth = 1200;
    private readonly List<WeakReference<Window>> _windows = [];

    [ObservableProperty] public partial string Theme { get; set; } = "system";
    [ObservableProperty] public partial string Density { get; set; } = "standard";
    [ObservableProperty] public partial bool ShowSize { get; set; } = true;
    [ObservableProperty] public partial bool ShowProgress { get; set; } = true;
    [ObservableProperty] public partial bool ShowSpeed { get; set; } = true;
    [ObservableProperty] public partial bool ShowTimeLeft { get; set; } = true;
    [ObservableProperty] public partial bool ShowAdded { get; set; } = true;

    public void Load(AppSettings store)
    {
        _store = store;
        Theme = store.Theme;
        Density = store.Density;
        ShowSize = store.ShowSize;
        ShowProgress = store.ShowProgress;
        ShowSpeed = store.ShowSpeed;
        ShowTimeLeft = store.ShowTimeLeft;
        ShowAdded = store.ShowAdded;
    }

    partial void OnThemeChanged(string value)
    {
        Save(s => s.Theme = value);
        foreach (var w in LiveWindows()) ApplyTheme(w);
    }

    partial void OnDensityChanged(string value)
    {
        Save(s => s.Density = value);
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(RowFontSize));
        OnPropertyChanged(nameof(IconSize));
    }

    partial void OnShowSizeChanged(bool value) => ColumnChanged(s => s.ShowSize = value);
    partial void OnShowProgressChanged(bool value) => ColumnChanged(s => s.ShowProgress = value);
    partial void OnShowSpeedChanged(bool value) => ColumnChanged(s => s.ShowSpeed = value);
    partial void OnShowTimeLeftChanged(bool value) => ColumnChanged(s => s.ShowTimeLeft = value);
    partial void OnShowAddedChanged(bool value) => ColumnChanged(s => s.ShowAdded = value);

    private void ColumnChanged(Action<AppSettings> save)
    {
        Save(save);
        RaiseColumns();
    }

    private void Save(Action<AppSettings> change)
    {
        if (_store is null) return;
        change(_store);
        try { _store.Save(); } catch { /* the setting still applies for this session */ }
    }

    // ----- density -----

    public double RowHeight => Density switch { "compact" => 32, "comfortable" => 52, _ => 42 };
    public double RowFontSize => Density switch { "compact" => 13, "comfortable" => 15, _ => 14 };
    public double IconSize => Density switch { "compact" => 20, "comfortable" => 32, _ => 24 };

    // ----- columns that fit -----

    /// <summary>The list's width; narrow windows drop the least important columns first.</summary>
    public void SetListWidth(double width)
    {
        if (Math.Abs(width - _listWidth) < 1) return;
        var before = (Fits(480), Fits(720), Fits(820), Fits(960), Fits(1080));
        _listWidth = width;
        if (before != (Fits(480), Fits(720), Fits(820), Fits(960), Fits(1080))) RaiseColumns();
    }

    private bool Fits(double minWidth) => _listWidth >= minWidth;

    public Visibility SizeColumn => Vis(ShowSize && Fits(480));
    public Visibility ProgressColumn => Vis(ShowProgress);
    public Visibility SpeedColumn => Vis(ShowSpeed && Fits(820));
    public Visibility TimeLeftColumn => Vis(ShowTimeLeft && Fits(960));
    public Visibility AddedColumn => Vis(ShowAdded && Fits(1080));

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseColumns()
    {
        foreach (var name in new[] { nameof(SizeColumn), nameof(ProgressColumn), nameof(SpeedColumn), nameof(TimeLeftColumn), nameof(AddedColumn) })
            OnPropertyChanged(name);
    }

    // ----- theme -----

    public ElementTheme ElementTheme => Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };

    /// <summary>Applies the theme to a window now and whenever it changes.</summary>
    public void Track(Window window)
    {
        _windows.Add(new WeakReference<Window>(window));
        window.Closed += (_, _) => _windows.RemoveAll(r => !r.TryGetTarget(out var w) || w == window);
        ApplyTheme(window);
    }

    private IEnumerable<Window> LiveWindows()
    {
        foreach (var r in _windows.ToList())
            if (r.TryGetTarget(out var w)) yield return w;
    }

    private void ApplyTheme(Window window)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = ElementTheme;
        window.AppWindow.TitleBar.PreferredTheme = Theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        OnPropertyChanged(nameof(ElementTheme));
        ThemeColors.RaiseChanged();
    }
}
