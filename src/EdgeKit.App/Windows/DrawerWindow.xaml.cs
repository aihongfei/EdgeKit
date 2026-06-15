using System;
using System.IO;
using EdgeKit.App.Commands;
using EdgeKit.App.Interaction;
using EdgeKit.App.ViewModels;
using EdgeKit.App.Windows.Backdrop;
using EdgeKit.Core.Commands;
using EdgeKit.Core.QuickLaunch;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Services;
using EdgeKit.Core.Agent;
using EdgeKit.Services.Diagnostics;
using EdgeKit.Services.Images;
using EdgeKit.Services.Text;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace EdgeKit.App.Windows;

/// <summary>
/// EdgeKit 左侧边缘抽屉窗口。
/// 视觉：无边框 + Acrylic 毛玻璃（常驻激活）+ 圆角 + 自定义标题栏 + 左侧停靠 + 置顶 + 不进任务栏。
/// 交互：边缘触发滑出、鼠标离开自动收起、固定（固定后不自动收起）、滑入/滑出动画、全屏应用中禁用。
///
/// 实现按职责拆分到多个 partial 文件：
/// - DrawerWindow.Geometry.cs   停靠几何与有效宽度
/// - DrawerWindow.Animation.cs  滑入滑出与宽度过渡动画
/// - DrawerWindow.AutoHide.cs   边缘触发与自动收起
/// - DrawerWindow.Menu.cs       侧边菜单构建/选中/展开收缩
/// - DrawerWindow.Interaction.cs 拖拽调宽、设置变更、固定/关闭
/// </summary>
public sealed partial class DrawerWindow : Window
{
    private enum DrawerState
    {
        Hidden,
        SlidingIn,
        Shown,
        SlidingOut
    }

    private readonly ISettingsService _settings;
    private readonly DrawerShellViewModel _shellViewModel;
    private readonly IRecentItemsRepository _recentItems;
    private readonly IQuickLaunchRepository _quickLaunchItems;
    private readonly QuickLaunchActionService _quickLaunchActions;
    private readonly HomeViewModel _homeViewModel;
    private readonly ForegroundWindowMonitor _windowMonitor;
    private readonly Search.SearchCoordinator _searchCoordinator;
    private readonly Search.InstalledAppIndex _appIndex;
    private readonly CommandRegistry _commandRegistry;
    private readonly CommandExecutor _commandExecutor;
    private readonly ICustomCommandRepository _customCommands;
    private readonly EdgeKit.Services.Clipboard.ClipboardService _clipboardService;
    private readonly ViewModels.ClipboardHistoryViewModel _clipboardViewModel;
    private readonly SystemDiagnosticsService _systemDiagnostics;
    private readonly HostsFileService _hostsFileService;
    private readonly EnvironmentVariableService _environmentVariables;
    private readonly WindowManagementService _windowManagement;
    private readonly FileLockService _fileLocks;
    private readonly ImageProcessingService _imageTools;
    private readonly TextProcessingService _textTools;
    private readonly IAgentService _agentService;

    // 抽屉基础宽度（收缩态，物理像素）下限。
    private const int MinDrawerWidth = 360;
    private const double MaxDrawerScreenRatio = 0.8;

    // 侧边菜单展开/收缩宽度（需与 XAML 的 OpenPaneLength / CompactPaneLength 一致，单位 DIP）。
    private const double OpenPaneLengthDip = 200;
    private const double CompactPaneLengthDip = 48;

    // 菜单项 -> 工具 Id 的映射，用于选中事件路由。
    private System.Collections.Generic.IReadOnlyDictionary<NavigationViewItem, string> _menuItemTools =
        new System.Collections.Generic.Dictionary<NavigationViewItem, string>();

    // 毛玻璃由独立控制器手动接管，保证窗口失焦时也保持磨砂效果。
    private readonly AcrylicBackdropController _backdrop = new();

    // 边缘触发与动画/自动收起所需的计时器。
    private readonly EdgeTriggerMonitor _edgeMonitor;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly DispatcherQueueTimer _widthAnimTimer;
    private readonly DispatcherQueueTimer _focusSearchTimer;

    private DrawerState _state = DrawerState.Hidden;
    private bool _isPinned;
    private bool _isResizing;
    private bool _keepOpenForSearchInput;
    private bool _searchKeepOpenSawDrawerForeground;
    private long _searchKeepOpenStartedTicks;

    // 窗口句柄，用于 DWM 去边框等原生操作。
    private nint _hwnd;

    // 停靠几何：抽屉显示时的目标位置 / 完全隐藏时的屏幕外位置。
    private int _dockedX;
    private int _hiddenX;
    private int _dockY;
    private int _dockWidth;
    private int _dockHeight;
    private EdgeTriggerSide _currentSide = EdgeTriggerSide.Left;
    private NativeMethods.RECT? _pendingWorkArea;

    // 动画状态。
    private long _animStartTicks;
    private int _animFromX;
    private int _animToX;

    // 宽度过渡动画状态（菜单展开/收起时窗口平滑变宽）。
    private long _widthAnimStartTicks;
    private int _widthAnimFrom;
    private int _widthAnimTo;
    private int _widthAnimRightEdge;

    // 光标离开抽屉的累计判断起点；为 null 表示光标仍在抽屉内或抽屉非展开态。
    private long? _cursorOutsideSinceTicks;

    private AppWindow AppWindowRef => AppWindow;

    public DrawerWindow(
        ISettingsService settings,
        DrawerShellViewModel shellViewModel,
        IRecentItemsRepository recentItems,
        IQuickLaunchRepository quickLaunchItems,
        QuickLaunchActionService quickLaunchActions,
        HomeViewModel homeViewModel,
        ForegroundWindowMonitor windowMonitor,
        Search.SearchCoordinator searchCoordinator,
        Search.InstalledAppIndex appIndex,
        CommandRegistry commandRegistry,
        CommandExecutor commandExecutor,
        ICustomCommandRepository customCommands,
        EdgeKit.Services.Clipboard.ClipboardService clipboardService,
        ViewModels.ClipboardHistoryViewModel clipboardViewModel,
        SystemDiagnosticsService systemDiagnostics,
        HostsFileService hostsFileService,
        EnvironmentVariableService environmentVariables,
        WindowManagementService windowManagement,
        FileLockService fileLocks,
        ImageProcessingService imageTools,
        TextProcessingService textTools,
        IAgentService agentService)
    {
        _settings = settings;
        _shellViewModel = shellViewModel;
        _recentItems = recentItems;
        _quickLaunchItems = quickLaunchItems;
        _quickLaunchActions = quickLaunchActions;
        _homeViewModel = homeViewModel;
        _windowMonitor = windowMonitor;
        _searchCoordinator = searchCoordinator;
        _appIndex = appIndex;
        _commandRegistry = commandRegistry;
        _commandExecutor = commandExecutor;
        _customCommands = customCommands;
        _clipboardService = clipboardService;
        _clipboardViewModel = clipboardViewModel;
        _systemDiagnostics = systemDiagnostics;
        _hostsFileService = hostsFileService;
        _environmentVariables = environmentVariables;
        _windowManagement = windowManagement;
        _fileLocks = fileLocks;
        _imageTools = imageTools;
        _textTools = textTools;
        _agentService = agentService;
        InitializeComponent();

        // 用全局快捷键设置初始化搜索框提示。
        UpdateHotkeyHint();

        // 无边框 + 自定义标题栏：把内容延伸进标题栏区域。
        // 不设拖拽区——抽屉窗口不应被用户拖动移动位置。
        ExtendsContentIntoTitleBar = true;

        ConfigureAppWindow();
        ComputeDockGeometry();
        _backdrop.Attach(this);
        BuildMenu();
        InitializeSearch();

        // 后台预热本机应用索引，供搜索合并匹配。
        _appIndex.Prewarm();

        // 初始展开状态与设置同步，并据此重算窗口宽度。
        ToolNav.IsPaneOpen = _settings.MenuExpanded;
        ApplyEffectiveWidth();

        // 设置变更（如设置页改宽）时实时反映到窗口。
        _settings.Changed += OnSettingsChanged;
        _searchCoordinator.SetEverythingEnabled(_settings.UseEverythingSearch);
        UpdateSearchSourceVisuals();

        var queue = DispatcherQueue;

        _animationTimer = queue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(15);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += OnAnimationTick;

        _autoHideTimer = queue.CreateTimer();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(60);
        _autoHideTimer.IsRepeating = true;
        _autoHideTimer.Tick += OnAutoHideTick;

        _widthAnimTimer = queue.CreateTimer();
        _widthAnimTimer.Interval = TimeSpan.FromMilliseconds(15);
        _widthAnimTimer.IsRepeating = true;
        _widthAnimTimer.Tick += OnWidthAnimTick;

        _focusSearchTimer = queue.CreateTimer();
        _focusSearchTimer.Interval = TimeSpan.FromMilliseconds(80);
        _focusSearchTimer.IsRepeating = false;
        _focusSearchTimer.Tick += OnFocusSearchTimerTick;

        _edgeMonitor = new EdgeTriggerMonitor(_settings, queue);
        _edgeMonitor.HandleClicked += OnEdgeHandleClicked;
        // 抽屉已可见（非 Hidden）时抑制边缘长条触发。
        _edgeMonitor.SetDrawerVisibleProbe(() => _state != DrawerState.Hidden);

        // 初始：移到屏幕外并隐藏，等待边缘触发。
        AppWindowRef.Move(new PointInt32(_hiddenX, _dockY));
        AppWindowRef.Hide();
        _edgeMonitor.Start();

        // 前台窗口监视：被动接收应用切换，记录最近活动窗口。
        _windowMonitor.Start(queue);

        // 剪贴板监听：在抽屉窗口句柄上注册，隐藏时也能捕获。
        _clipboardService.Start(_hwnd);

        Closed += OnClosed;
    }

    private void ConfigureAppWindow()
    {
        var appWindow = AppWindowRef;

        // 不在任务栏 / Alt-Tab 中显示。
        appWindow.IsShownInSwitchers = false;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 无边框：隐藏标题栏、边框和系统按钮。
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 去掉残留的非客户区白色边框：子类化窗口拦截 WM_NCCALCSIZE，
        // 同时用 DWM 保留窗口圆角。
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ScreenInterop.RemoveWindowBorder(_hwnd);
        WindowBorderRemover.Apply(_hwnd);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
        _edgeMonitor.HandleClicked -= OnEdgeHandleClicked;
        _edgeMonitor.Dispose();
        _windowMonitor.Dispose();
        _clipboardService.Dispose();
        _searchCoordinator.EverythingStateChanged -= OnEverythingStateChanged;
        _searchCancellationSource?.Cancel();
        _searchCancellationSource?.Dispose();
        _animationTimer.Stop();
        _autoHideTimer.Stop();
        _widthAnimTimer.Stop();
        _focusSearchTimer.Stop();

        _backdrop.Dispose();
    }

    /// <summary>
    /// 用全局快捷键设置更新搜索框提示文本。
    /// </summary>
    private void UpdateHotkeyHint()
    {
        if (HotkeyGesture.TryParse(_settings.GlobalHotkeyText, out var gesture))
        {
            HotkeyHintText.Text = gesture.ToString().Replace('+', ' ');
        }
        else
        {
            HotkeyHintText.Text = _settings.GlobalHotkeyText ?? string.Empty;
        }
    }
}
