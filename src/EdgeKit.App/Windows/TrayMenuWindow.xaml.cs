using System;
using System.Collections.Generic;
using System.Linq;
using EdgeKit.App.Interaction;
using EdgeKit.App.Windows.Backdrop;
using EdgeKit.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace EdgeKit.App.Windows;

/// <summary>
/// 托盘右键 XAML 弹出菜单：圆角 Acrylic 暗色背景、图标 + 文字、失焦关闭。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private readonly IReadOnlyList<TrayMenuItem> _menuItems;
    private readonly AcrylicBackdropController _backdrop = new();

    public TrayMenuWindow(IReadOnlyList<TrayMenuItem> menuItems)
    {
        _menuItems = menuItems;

        InitializeComponent();
        ConfigureWindow();

        MenuItemsControl.ItemsSource = menuItems;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        RootGrid.Loaded += OnWindowLoaded;

        _backdrop.Attach(this);
        _backdrop.SetTintColor(Color.FromArgb(255, 8, 9, 10));
    }

    private void ConfigureWindow()
    {
        // 不使用系统标题栏和边框，由 XAML 完全绘制。
        ExtendsContentIntoTitleBar = false;

        var appWindow = AppWindow;
        appWindow.IsShownInSwitchers = false;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // 初始给个极小尺寸，加载后按内容测量再定位。
        appWindow.ResizeClient(new SizeInt32(10, 10));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.ScreenInterop.RemoveWindowBorder(hwnd);
        Native.WindowBorderRemover.Apply(hwnd, enableResize: false);
    }

    /// <summary>
    /// 显示菜单并强制其获得前台焦点，确保 Window.Activated 能收到 Deactivated 事件。
    /// </summary>
    public void ShowAndActivate()
    {
        Activate();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != nint.Zero)
        {
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 测量内容所需逻辑尺寸。
        RootBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = RootBorder.DesiredSize;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;

        var width = (int)Math.Ceiling(desired.Width * scale);
        var height = (int)Math.Ceiling(desired.Height * scale);
        width = Math.Max(width, 160);
        height = Math.Max(height, 32);

        var (x, y) = CalculatePosition(width, height);

        AppWindow.Move(new PointInt32(x, y));
        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    private static (int X, int Y) CalculatePosition(int width, int height)
    {
        var cursor = ScreenInterop.GetCursorPosition();
        var workArea = ScreenInterop.GetWorkAreaAtCursor();

        // 默认让菜单左上角对齐光标位置，与原生右键菜单一致。
        var x = cursor.X;
        var y = cursor.Y;

        if (x + width > workArea.Right)
        {
            x = workArea.Right - width;
        }

        if (y + height > workArea.Bottom)
        {
            y = workArea.Bottom - height;
        }

        x = Math.Max(x, workArea.Left);
        y = Math.Max(y, workArea.Top);

        return (x, y);
    }

    private void OnMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not uint id)
        {
            return;
        }

        var item = _menuItems.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            return;
        }

        // 先关闭窗口再执行动作，避免焦点变化导致执行被延后或打断。
        Close();
        item.Execute?.Invoke();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Close();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
        RootGrid.Loaded -= OnWindowLoaded;
        _backdrop.Dispose();
    }
}

/// <summary>
/// 托盘菜单项模板选择器：命令项显示图标 + 文字按钮，分隔线显示横线。
/// </summary>
public sealed class TrayMenuItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CommandTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not TrayMenuItem menuItem)
        {
            return null;
        }

        return menuItem.Kind == TrayMenuItemKind.Separator
            ? SeparatorTemplate
            : CommandTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
