using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using OpenDownloadManager.Models;
using OpenDownloadManager.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace OpenDownloadManager.Views;

/// <summary>
/// The window once the download has ended: a check (or a cross) and the outcome, then the file
/// itself as a card that opens on double-click and can be dragged into another app, then where it
/// went and where it came from. A failed download keeps the same layout with the reason and Retry.
/// </summary>
public sealed partial class DownloadWindow
{
    /// <summary>Where the file name starts in the card: padding, icon, gap. The rows' values line up with it.</summary>
    private const double ValueColumn = 12 + 48 + 16;

    private StackPanel _endBody = null!;
    private StackPanel _endRest = null!;
    private Grid _badge = null!;
    private Ellipse _badgeDisc = null!;
    private Microsoft.UI.Xaml.Shapes.Path _badgeMark = null!;
    private TextBlock _endTitle = null!, _endSub = null!, _endHint = null!;
    private Button _fileCard = null!;
    private Image _fileImage = null!;
    private FontIcon _fileGlyph = null!;
    private TextBlock _fileName = null!, _fileMeta = null!;
    private Button _folderButton = null!, _sourceButton = null!;
    private TextBlock _folderValue = null!, _sourceValue = null!, _sourceLabel = null!;
    private Grid _afterRow = null!;
    private FontIcon _afterIcon = null!;
    private TextBlock _afterText = null!;
    private ProgressRing _afterRing = null!;
    private Button _afterButton = null!;
    private Button _more = null!, _endPrimary = null!, _endSecondary = null!, _endClose = null!;
    private bool _cardPointer;
    private string? _iconFor;

    /// <summary>The archive's extraction: null when none ran, "" while running, else the folder ("!" when it failed).</summary>
    private string? _extracted;

    private static bool Motion => ViewSettings.Current.Animation != "off" && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private static readonly FontFamily Display = new("Segoe UI Variable Display");

    private UIElement BuildEnd()
    {
        // ----- the outcome: a 24 px badge, the title and one line under it -----
        _badgeDisc = new Ellipse { Width = 24, Height = 24 };
        _badgeMark = new Microsoft.UI.Xaml.Shapes.Path
        {
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Style = ThemedStyles.Get("Path", null, ("Stroke", "TextOnAccentFillColorPrimaryBrush")),
        };
        _badge = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, Children = { _badgeDisc, _badgeMark } };
        _endTitle = new TextBlock { FontFamily = Display, FontSize = 20, LineHeight = 28, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _endSub = new TextBlock { Style = TextStyle(false, true), TextWrapping = TextWrapping.Wrap };
        _endHint = new TextBlock { Style = TextStyle(true, true), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        head.Children.Add(_badge);
        Grid.SetColumn(_endTitle, 1);
        head.Children.Add(_endTitle);
        Grid.SetRow(_endSub, 1);
        Grid.SetColumn(_endSub, 1);
        head.Children.Add(_endSub);
        Grid.SetRow(_endHint, 2);
        Grid.SetColumn(_endHint, 1);
        head.Children.Add(_endHint);

        // ----- the file: icon, name, type and size; double-click opens, drag copies it out -----
        _fileImage = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform };
        _fileGlyph = new FontIcon { FontSize = 32, Style = ThemedStyles.Get("FontIcon", null, ("Foreground", "TextFillColorSecondaryBrush")) };
        _fileName = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Style = TextStyle(false, false) };
        _fileMeta = Text(caption: true, secondary: true);
        var card = new Grid { ColumnSpacing = 16 };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(new Grid { Width = 48, Height = 48, Children = { _fileGlyph, _fileImage } });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _fileName, _fileMeta } };
        Grid.SetColumn(names, 1);
        card.Children.Add(names);
        _fileCard = new Button
        {
            Content = card,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            CanDrag = true,
        };
        _fileCard.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _cardPointer = true), true);
        // A click with the mouse only selects (a stray click shouldn't start a program); Enter or Space opens.
        _fileCard.Click += (_, _) =>
        {
            if (_cardPointer) { _cardPointer = false; return; }
            PrimaryAction();
        };
        _fileCard.DoubleTapped += (_, _) => { if (_item.IsCompleted) OpenFile(); };
        _fileCard.DragStarting += OnCardDragStarting;

        // ----- where it went and where it came from -----
        _folderValue = new TextBlock { TextWrapping = TextWrapping.NoWrap, Style = ThemedStyles.Get("TextBlock", "BodyTextBlockStyle", ("Foreground", "AccentTextFillColorPrimaryBrush")) };
        _sourceValue = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Style = TextStyle(false, false) };
        _sourceLabel = Text(secondary: true, text: "Source");
        var rows = new Grid { RowSpacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumn) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _folderButton = AddRow(rows, Text(secondary: true, text: "Folder"), _folderValue, "Copy the folder path", () => _item.SaveDir,
            () => Shell.ShowInFolder(_item.IsCompleted ? _item.FilePath : null, _item.SaveDir));
        _sourceButton = AddRow(rows, _sourceLabel, _sourceValue, "Copy the link", () => _item.Url, OpenSource);
        _folderButton.SizeChanged += (_, _) => FitFolder();

        // ----- what ran after the download (an archive extracted) -----
        _afterIcon = new FontIcon { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        _afterRing = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        _afterText = new TextBlock { Style = TextStyle(false, true), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _afterButton = new Button { Content = "Open folder", VerticalAlignment = VerticalAlignment.Center };
        _afterButton.Click += (_, _) => { if (_extracted is { Length: > 1 } f) Shell.ShowInFolder(null, f); };
        _afterRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _afterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueColumn - 12 - 12) });
        _afterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _afterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var afterLead = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { _afterIcon, _afterRing } };
        _afterRow.Children.Add(afterLead);
        Grid.SetColumn(_afterText, 1);
        _afterRow.Children.Add(_afterText);
        Grid.SetColumn(_afterButton, 2);
        _afterRow.Children.Add(_afterButton);

        // ----- footer: more on the left, the main actions on the right -----
        _more = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 16 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
        };
        ToolTipService.SetToolTip(_more, "More");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_more, "More options");
        _more.Click += (_, _) => MoreMenu().ShowAt(_more, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft });
        _endPrimary = new Button { Style = Res<Style>("AccentButtonStyle") };
        _endPrimary.Click += (_, _) => PrimaryAction();
        _endSecondary = new Button();
        _endSecondary.Click += (_, _) => SecondaryAction();
        _endClose = new Button { Content = "Close" };
        _endClose.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _endPrimary, _endSecondary, _endClose } };
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0), Children = { _more, actions } };

        _endRest = new StackPanel { Children = { _fileCard, rows, _afterRow } };
        _endBody = new StackPanel
        {
            Padding = new Thickness(20, 16, 20, 20),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Children = { head, new Border { Height = 16 }, _endRest, footer },
        };
        return _endBody;
    }

    /// <summary>A label, then a value that is a quiet button (lit on hover) with a copy button that shows on hover or focus.</summary>
    private static Button AddRow(Grid rows, TextBlock label, TextBlock value, string copyTip, Func<string> copyText, Action click)
    {
        var row = rows.RowDefinitions.Count;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        label.Padding = new Thickness(12, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(label, row);
        rows.Children.Add(label);

        var button = new Button
        {
            Content = value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 40, 6),
            Margin = new Thickness(-8, 0, 0, 0),
            MinHeight = 32,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) => click();
        var copy = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Opacity = 0,
        };
        ToolTipService.SetToolTip(copy, copyTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, copyTip);
        copy.Click += (_, _) =>
        {
            Copy(copyText());
            ToolTipService.SetToolTip(copy, "Copied");
        };
        var cell = new Grid { Children = { button, copy } };
        var inside = false;
        void Show() => copy.Opacity = inside || button.FocusState != FocusState.Unfocused || copy.FocusState != FocusState.Unfocused ? 1 : 0;
        cell.PointerEntered += (_, _) => { inside = true; Show(); };
        cell.PointerExited += (_, _) => { inside = false; Show(); ToolTipService.SetToolTip(copy, copyTip); };
        button.GotFocus += (_, _) => Show();
        button.LostFocus += (_, _) => Show();
        copy.GotFocus += (_, _) => Show();
        copy.LostFocus += (_, _) => Show();
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        rows.Children.Add(cell);
        return button;
    }

    /// <summary>Fills the end view from the download; <paramref name="animate"/> on the first showing.</summary>
    private void ShowEnd(bool animate)
    {
        var i = _item;
        var done = i.IsCompleted;
        PaintBadge();
        _endTitle.Text = done ? "Download complete" : i.LinkExpired ? "Link expired" : "Download failed";

        var size = i.TotalSize ?? i.Downloaded;
        if (done)
        {
            // How long it took and how fast; the size is in the file card below.
            var seconds = i.CreatedAt > 0 && i.FinishedAt > i.CreatedAt ? (i.FinishedAt - i.CreatedAt) / 1000.0 : 0;
            var history = SpeedHistory.Get(i.Id);
            var speed = history.Count > 0 ? (long)history.Average(h => h.Speed) : seconds > 0 ? (long)(size / seconds) : 0;
            var parts = new List<string>();
            if (seconds > 0) parts.Add(Format.Duration(seconds));
            if (speed > 0) parts.Add($"{Format.Speed(speed)} average");
            _endSub.Text = string.Join("  ·  ", parts);
            _endSub.Style = TextStyle(false, true);
            _endHint.Text = "";
        }
        else
        {
            _endSub.Text = string.IsNullOrWhiteSpace(i.Error) ? "The download stopped." : i.Error!;
            _endSub.Style = ThemedStyles.Get("TextBlock", "BodyTextBlockStyle", ("Foreground", "SystemFillColorCriticalBrush"));
            _endHint.Text = i.LinkExpired
                ? "Open the page again to get a fresh link, then retry."
                : i.Downloaded > 0 && i.TotalSize is > 0 && (i.Resumable || i.CanResume)
                    ? $"{Format.Bytes(i.Downloaded)} of {Format.Bytes(i.TotalSize.Value)} arrived. Retry picks up where it stopped."
                    : "";
        }
        _endSub.Visibility = _endSub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _endHint.Visibility = _endHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The file card.
        _fileName.Text = i.DisplayName;
        var type = FileIcons.TypeName(i.FileName) ?? i.Category;
        _fileMeta.Text = done
            ? (size > 0 ? $"{type}  ·  {Format.Bytes(size)}" : type)
            : i.TotalSize is > 0 ? $"{Format.Bytes(i.Downloaded)} of {Format.Bytes(i.TotalSize.Value)}" : type;
        _fileCard.Opacity = done ? 1 : 0.7;
        _fileCard.CanDrag = done;
        ToolTipService.SetToolTip(_fileCard, done ? $"{i.DisplayName}\nDouble-click to open, or drag it into another app" : i.DisplayName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_fileCard, done ? $"Open {i.DisplayName}" : i.DisplayName);
        _fileGlyph.Glyph = i.Glyph;
        _ = LoadFileIcon();

        // The rows.
        FitFolder();
        ToolTipService.SetToolTip(_folderButton, $"Show in folder\n{(done ? i.FilePath : i.SaveDir) ?? i.SaveDir}");
        _sourceValue.Text = i.Url;
        ToolTipService.SetToolTip(_sourceButton, string.IsNullOrEmpty(i.PageUrl) ? i.Url : $"Open the page\n{i.PageUrl}\n\nLink: {i.Url}");
        UpdateAfter();

        // The buttons.
        _endPrimary.Content = done ? "Open" : i.LinkExpired && !string.IsNullOrEmpty(i.PageUrl) ? "Open page" : "Retry";
        _endSecondary.Content = done ? "Show in folder" : "Retry";
        _endSecondary.Visibility = done || (i.LinkExpired && !string.IsNullOrEmpty(i.PageUrl)) ? Visibility.Visible : Visibility.Collapsed;
        _endPrimary.IsEnabled = done || i.CanResume || !string.IsNullOrEmpty(i.PageUrl);

        if (!animate) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _endPrimary.Focus(FocusState.Programmatic));
        if (!Motion) return;
        // The badge springs in while its mark draws, then the rest rises into place.
        var visual = ElementCompositionPreview.GetElementVisual(_badge);
        var compositor = visual.Compositor;
        visual.CenterPoint = new System.Numerics.Vector3(12, 12, 0);
        var grow = compositor.CreateSpringVector3Animation();
        grow.InitialValue = new System.Numerics.Vector3(0.4f, 0.4f, 1);
        grow.FinalValue = new System.Numerics.Vector3(1, 1, 1);
        grow.DampingRatio = 0.55f;
        grow.Period = TimeSpan.FromMilliseconds(55);
        visual.StartAnimation("Scale", grow);
        _badgeMark.StrokeDashArray = [10];
        var draw = new DoubleAnimation
        {
            From = 10,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(140),
            Duration = TimeSpan.FromMilliseconds(280),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(draw, _badgeMark);
        Storyboard.SetTargetProperty(draw, "StrokeDashOffset");
        _badgeMark.StrokeDashOffset = 10;
        var board = new Storyboard { Children = { draw } };
        board.Completed += (_, _) => { _badgeMark.StrokeDashOffset = 0; _badgeMark.StrokeDashArray = null; };
        board.Begin();

        var rest = ElementCompositionPreview.GetElementVisual(_endRest);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.DelayTime = TimeSpan.FromMilliseconds(100);
        fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        fade.Duration = TimeSpan.FromMilliseconds(260);
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new System.Numerics.Vector3(0, 6, 0));
        rise.InsertKeyFrame(1, System.Numerics.Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f)));
        rise.DelayTime = fade.DelayTime;
        rise.DelayBehavior = fade.DelayBehavior;
        rise.Duration = fade.Duration;
        rest.StartAnimation("Opacity", fade);
        rest.StartAnimation("Offset", rise);
    }

    /// <summary>A green disc with a check when done, a red one with a cross when failed.</summary>
    private void PaintBadge()
    {
        if (_badgeDisc is null) return;
        var done = _item.IsCompleted;
        _badgeDisc.Style = ThemedStyles.Get("Ellipse", null, ("Fill", done ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"));
        var figures = new PathFigureCollection();
        PathFigure Line(params Point[] points)
        {
            var f = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
            for (var k = 1; k < points.Length; k++) f.Segments.Add(new LineSegment { Point = points[k] });
            return f;
        }
        if (done) figures.Add(Line(new(7, 12.4), new(10.3, 15.6), new(17, 8.8)));
        else
        {
            figures.Add(Line(new(8.5, 8.5), new(15.5, 15.5)));
            figures.Add(Line(new(15.5, 8.5), new(8.5, 15.5)));
        }
        _badgeMark.Data = new PathGeometry { Figures = figures };
    }

    private async Task LoadFileIcon()
    {
        var key = $"{_item.FileName}|{_item.FilePath}|{_item.IsCompleted}";
        if (key == _iconFor) return;
        _iconFor = key;
        // Twice the size, so it stays sharp at 150% and 200%.
        var icon = await FileIcons.ForAsync(_item.FileName, _item.IsCompleted ? _item.FilePath : null, 96);
        if (_iconFor != key) return;
        _fileImage.Source = icon;
        _fileGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The folder path, shortened in the middle so its last folder stays readable.</summary>
    private void FitFolder()
    {
        if (_folderValue is null) return;
        var path = _item.SaveDir ?? "";
        var width = _folderButton.ActualWidth - _folderButton.Padding.Left - _folderButton.Padding.Right;
        _folderValue.Text = width > 0 ? MiddleTrim(path, width) : path;
    }

    private string MiddleTrim(string path, double width)
    {
        var probe = new TextBlock { Style = _folderValue.Style, FontSize = _folderValue.FontSize };
        bool Fits(string s)
        {
            probe.Text = s;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width <= width;
        }
        if (Fits(path)) return path;
        var parts = path.TrimEnd('\\').Split('\\');
        if (parts.Length < 2) return path;
        var last = parts[^1];
        for (var keep = parts.Length - 2; keep >= 1; keep--)
        {
            var s = string.Join('\\', parts[..keep]) + "\\…\\" + last;
            if (Fits(s)) return s;
        }
        return "…\\" + last;
    }

    private void OnExtractChanged(string archive, string? folder, bool ok)
    {
        if (!string.Equals(archive, _item.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _extracted = folder is null ? "" : ok ? folder : "!";
            UpdateAfter();
            FitHeight();
        });
    }

    private void UpdateAfter()
    {
        if (_afterRow is null) return;
        _afterRow.Visibility = _extracted is null || !_item.IsCompleted ? Visibility.Collapsed : Visibility.Visible;
        if (_extracted is null) return;
        var running = _extracted.Length == 0;
        var failed = _extracted == "!";
        _afterRing.IsActive = running;
        _afterRing.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _afterIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _afterIcon.Glyph = failed ? "" : "";
        _afterIcon.Style = ThemedStyles.Get("FontIcon", null, ("Foreground", failed ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush"));
        _afterText.Text = running ? "Extracting the archive…"
            : failed ? "Couldn't extract the archive"
            : $"Extracted to “{System.IO.Path.GetFileName(_extracted)}”";
        ToolTipService.SetToolTip(_afterText, running || failed ? null : _extracted);
        _afterButton.Visibility = running || failed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ----- actions -----

    private void PrimaryAction()
    {
        var i = _item;
        if (i.IsCompleted) OpenFile();
        else if (i.LinkExpired && !string.IsNullOrEmpty(i.PageUrl)) Shell.Open(i.PageUrl);
        else if (i.CanResume) _ = TogglePause();
    }

    private void SecondaryAction()
    {
        if (_item.IsCompleted) Shell.ShowInFolder(_item.FilePath, _item.SaveDir);
        else _ = TogglePause();
    }

    private void OpenFile()
    {
        if (_item.FilePath is { } p) Shell.Open(p);
        Close();
    }

    private void OpenSource()
    {
        // The page the link came from; without one, opening the link would only download it again.
        if (!string.IsNullOrEmpty(_item.PageUrl)) Shell.Open(_item.PageUrl);
        else Copy(_item.Url);
    }

    private static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private async void OnCardDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (!_item.IsCompleted || _item.FilePath is not { } path || !System.IO.File.Exists(path))
        {
            args.Cancel = true;
            return;
        }
        var deferral = args.GetDeferral();
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems([file]);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
        }
        catch
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private MenuFlyout MoreMenu()
    {
        var i = _item;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action click, string? keys = null)
        {
            var m = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys is not null) m.KeyboardAcceleratorTextOverride = keys;
            m.Click += (_, _) => click();
            menu.Items.Add(m);
            return m;
        }
        if (i.IsCompleted)
        {
            var exists = i.FilePath is { } p && System.IO.File.Exists(p);
            Item("Open with…", "", () => Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {i.FilePath}"))).IsEnabled = exists;
            Item("Copy file path", "", () => Copy(i.FilePath ?? ""), "Ctrl+C");
            Item("Copy download link", "", () => Copy(i.Url));
            menu.Items.Add(new MenuFlyoutSeparator());
            Item("Download again", "", async () => await _core.CallAsync(new { type = "redownload", id = i.Id }));
            Item("Delete file…", "", async () => await DeleteFile()).IsEnabled = exists;
        }
        else
        {
            Item("Copy download link", "", () => Copy(i.Url));
            if (!string.IsNullOrEmpty(i.PageUrl)) Item("Open the download page", "", () => Shell.Open(i.PageUrl));
            menu.Items.Add(new MenuFlyoutSeparator());
            Item("Remove from list", "", async () =>
            {
                await _core.CallAsync(new { type = "remove", id = i.Id, deleteFile = false });
                Close();
            });
        }
        return menu;
    }

    private async Task DeleteFile()
    {
        var dialog = new ContentDialog
        {
            Title = "Delete the file?",
            Content = $"\"{_item.DisplayName}\" will be deleted from your computer, not only removed from the list.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _core.CallAsync(new { type = "remove", id = _item.Id, deleteFile = true });
        Close();
    }

    /// <summary>Esc closes; Enter does the main action when focus isn't on a button; Ctrl+C copies the file's path.</summary>
    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var ended = _endBody.Visibility == Visibility.Visible;
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (ended && e.Key == VirtualKey.Enter && e.OriginalSource is not ButtonBase)
        {
            PrimaryAction();
            e.Handled = true;
        }
        else if (ended && _item.IsCompleted && e.Key == VirtualKey.C
            && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            Copy(_item.FilePath ?? "");
            e.Handled = true;
        }
    }
}
