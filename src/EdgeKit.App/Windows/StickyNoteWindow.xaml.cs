using EdgeKit.App.Notes;
using EdgeKit.App.Windows.Backdrop;
using EdgeKit.Core.Notes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace EdgeKit.App.Windows;

/// <summary>
/// 独立桌面便签窗口：Acrylic 半透明黑色系、无边框、可拖拽、可调整大小。
/// 关闭窗口不会删除便签，仅隐藏；可通过管理页重新打开。
/// </summary>
public sealed partial class StickyNoteWindow : Window
{
    private readonly IStickyNoteRepository _repository;
    private readonly AcrylicBackdropController _backdrop = new();
    private readonly TextBlock _contentMeasureBlock = new();
    private readonly DispatcherQueueTimer _saveDebounceTimer;
    private readonly Action<StickyNoteWindow> _onCloseRequested;

    private StickyNote _note;
    private bool _isInitializing = true;
    private bool _isSaving;

    public StickyNoteWindow(
        StickyNote note,
        IStickyNoteRepository repository,
        Action<StickyNoteWindow> onCloseRequested)
    {
        _note = note;
        _repository = repository;
        _onCloseRequested = onCloseRequested;

        InitializeComponent();
        ConfigureContentMeasurement();

        // 自定义标题栏：标题栏区域可被拖拽，其余区域为内容。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        ConfigureAppWindow();

        // 必须在 ApplyNoteToUi 之前创建防抖计时器：
        // 设置 TopmostToggle.IsChecked 会同步触发 Checked/Unchecked 事件，进而调用 SaveDeferred。
        _saveDebounceTimer = DispatcherQueue.CreateTimer();
        _saveDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveDebounceTimer.IsRepeating = false;
        _saveDebounceTimer.Tick += OnSaveDebounceTick;

        ApplyNoteToUi();
        UpdateBackground();

        _backdrop.Attach(this);

        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnWindowClosed;
        ContentScrollViewer.SizeChanged += OnContentScrollViewerSizeChanged;
        ContentBox.Loaded += OnContentBoxLoaded;
        ContentBox.BringIntoViewRequested += OnContentBoxBringIntoViewRequested;
    }

    public long NoteId => _note.Id;

    public StickyNote Note => _note;

    /// <summary>供外部更新模型后刷新 UI（例如管理页修改内容）。</summary>
    public void UpdateNote(StickyNote note)
    {
        _note = note;
        ApplyNoteToUi();
        ApplyPositionAndSize();
        UpdateBackground();
    }

    /// <summary>窗口显示后再应用位置和大小，避免未显示前 ResizeClient 被忽略。</summary>
    public void ApplyInitialBounds()
    {
        ApplyPositionAndSize();
    }

    private void ConfigureAppWindow()
    {
        var appWindow = AppWindow;

        // 显示在任务栏/Alt-Tab 中，取消置顶后仍能找回。
        appWindow.IsShownInSwitchers = true;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = _note.IsTopmost;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.ScreenInterop.RemoveWindowBorder(hwnd);
        Native.WindowBorderRemover.Apply(hwnd, enableResize: true);
    }

    private void ApplyNoteToUi()
    {
        ContentBox.Text = _note.Content;
        TopmostToggle.IsChecked = _note.IsTopmost;
    }

    private void ApplyPositionAndSize()
    {
        var appWindow = AppWindow;
        appWindow.Move(new PointInt32((int)_note.X, (int)_note.Y));

        var width = Math.Clamp((int)_note.Width, 180, 800);
        var height = Math.Clamp((int)_note.Height, 140, 600);
        appWindow.ResizeClient(new SizeInt32(width, height));

        _isInitializing = false;
    }

    private void UpdateBackground()
    {
        var color = ParseColor(_note.ColorHex);

        // 把便签颜色作为 Acrylic Tint，让窗口背景与桌面融合，呈现半透明黑色系效果。
        _backdrop.SetTintColor(color);
        RootGrid.Background = new SolidColorBrush(Colors.Transparent);
    }

    private void ConfigureContentMeasurement()
    {
        _contentMeasureBlock.TextWrapping = TextWrapping.Wrap;
        _contentMeasureBlock.FontSize = ContentBox.FontSize;
        _contentMeasureBlock.FontFamily = ContentBox.FontFamily;
        _contentMeasureBlock.FontWeight = ContentBox.FontWeight;
        _contentMeasureBlock.Padding = ContentBox.Padding;
    }

    private void ResizeContentBoxToText()
    {
        var viewportWidth = ContentScrollViewer.ViewportWidth > 0
            ? ContentScrollViewer.ViewportWidth
            : ContentScrollViewer.ActualWidth;
        var availableWidth = Math.Max(0, viewportWidth - ContentBox.Padding.Left - ContentBox.Padding.Right);
        if (availableWidth <= 0)
        {
            return;
        }

        _contentMeasureBlock.Width = availableWidth;
        _contentMeasureBlock.Text = string.IsNullOrEmpty(ContentBox.Text) ? " " : ContentBox.Text;
        _contentMeasureBlock.Measure(new Size(availableWidth, double.PositiveInfinity));

        ContentBox.Width = viewportWidth;
        ContentBox.Height = Math.Max(ContentScrollViewer.ActualHeight, _contentMeasureBlock.DesiredSize.Height + 24);
    }

    private void OnContentBoxLoaded(object sender, RoutedEventArgs e)
    {
        ResizeContentBoxToText();
    }

    private void OnContentScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeContentBoxToText();
    }

    private void OnContentBoxBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        args.Handled = true;
    }

    internal static Color ParseColor(string hex)
    {
        var span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length == 8
            && byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(span[6..8], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(a, r, g, b);
        }

        if (span.Length == 6
            && byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var rr)
            && byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var gg)
            && byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var bb))
        {
            return Color.FromArgb(255, rr, gg, bb);
        }

        return Color.FromArgb(255, 26, 26, 30);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isInitializing || _isSaving)
        {
            return;
        }

        var changed = false;
        var x = _note.X;
        var y = _note.Y;
        var width = _note.Width;
        var height = _note.Height;

        if (args.DidPositionChange)
        {
            x = sender.Position.X;
            y = sender.Position.Y;
            changed = true;
        }

        if (args.DidSizeChange)
        {
            width = sender.ClientSize.Width;
            height = sender.ClientSize.Height;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _note = _note with
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            UpdatedUtc = DateTime.UtcNow
        };

        SaveDeferred();
    }

    private void OnContentChanged(object sender, TextChangedEventArgs e)
    {
        ResizeContentBoxToText();
        _note = _note with { Content = ContentBox.Text, UpdatedUtc = DateTime.UtcNow };
        SaveDeferred();
    }

    private void OnTopmostChecked(object sender, RoutedEventArgs e)
    {
        SetTopmost(true);
    }

    private void OnTopmostUnchecked(object sender, RoutedEventArgs e)
    {
        SetTopmost(false);
    }

    private void SetTopmost(bool topmost)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = topmost;
        }

        _note = _note with { IsTopmost = topmost, UpdatedUtc = DateTime.UtcNow };
        SaveDeferred();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _onCloseRequested(this);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 拦截原生关闭，改为隐藏/移除窗口，不删除便签。
        args.Handled = true;
        _onCloseRequested(this);
    }

    private void SaveDeferred()
    {
        if (_saveDebounceTimer is null)
        {
            return;
        }

        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void OnSaveDebounceTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            _isSaving = true;
            _repository.Update(_note);
        }
        finally
        {
            _isSaving = false;
        }
    }

    public void DisposeResources()
    {
        _saveDebounceTimer.Stop();
        AppWindow.Changed -= OnAppWindowChanged;
        Closed -= OnWindowClosed;
        ContentScrollViewer.SizeChanged -= OnContentScrollViewerSizeChanged;
        ContentBox.Loaded -= OnContentBoxLoaded;
        ContentBox.BringIntoViewRequested -= OnContentBoxBringIntoViewRequested;

        _backdrop.Dispose();
    }
}