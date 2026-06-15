using System;
using EdgeKit.App.Commands;
using EdgeKit.App.Interaction;
using EdgeKit.App.ViewModels;
using EdgeKit.App.Windows;
using EdgeKit.Core.Commands;
using EdgeKit.Core.QuickLaunch;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Services;
using EdgeKit.Core.Settings;
using EdgeKit.Core.Tools;
using EdgeKit.Core.Agent;
using EdgeKit.Data.Agent;
using EdgeKit.Data.Commands;
using EdgeKit.Data.QuickLaunch;
using EdgeKit.Data.Recent;
using EdgeKit.Data.Settings;
using EdgeKit.Services.Agent;
using EdgeKit.Services.Settings;
using EdgeKit.Services.Diagnostics;
using EdgeKit.Services.Images;
using EdgeKit.Services.Text;
using EdgeKit.Services.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Serilog;

namespace EdgeKit.App;

/// <summary>
/// 应用入口。负责构建 DI 容器、初始化日志与全局异常处理，并创建抽屉窗口。
/// </summary>
public partial class App : Application
{
    private Window? _drawerWindow;
    private TrayIconService? _trayIcon;
    private GlobalHotkeyService? _globalHotkey;
    private bool _isExiting;

    /// <summary>全局服务提供者。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    internal static GlobalHotkeyService? GlobalHotkey { get; private set; }

    internal static bool IsRecordingGlobalHotkey { get; set; }

    public App()
    {
        InitializeComponent();

        ConfigureLogging();
        Services = ConfigureServices();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Information("EdgeKit starting");

        var internalCommandExitCode = TryRunInternalCommand();
        if (internalCommandExitCode is not null)
        {
            Log.CloseAndFlush();
            Environment.Exit(internalCommandExitCode.Value);
            return;
        }

        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Load();
        Services.GetRequiredService<IStartupLaunchService>().RefreshCommandIfEnabled();

        // 抽屉默认隐藏在屏幕外，由边缘触发滑出（构造函数内已启动边缘监视）。
        var shellViewModel = Services.GetRequiredService<DrawerShellViewModel>();
        var recentItems = Services.GetRequiredService<IRecentItemsRepository>();
        var quickLaunchItems = Services.GetRequiredService<IQuickLaunchRepository>();
        var quickLaunchActions = Services.GetRequiredService<QuickLaunchActionService>();
        var homeViewModel = Services.GetRequiredService<HomeViewModel>();
        var windowMonitor = Services.GetRequiredService<ForegroundWindowMonitor>();
        var searchCoordinator = Services.GetRequiredService<Search.SearchCoordinator>();
        var appIndex = Services.GetRequiredService<Search.InstalledAppIndex>();
        var commandRegistry = Services.GetRequiredService<CommandRegistry>();
        var commandExecutor = Services.GetRequiredService<CommandExecutor>();
        var customCommands = Services.GetRequiredService<ICustomCommandRepository>();
        var clipboardService = Services.GetRequiredService<EdgeKit.Services.Clipboard.ClipboardService>();
        var clipboardViewModel = Services.GetRequiredService<ClipboardHistoryViewModel>();
        var systemDiagnostics = Services.GetRequiredService<SystemDiagnosticsService>();
        var hostsFileService = Services.GetRequiredService<HostsFileService>();
        var environmentVariables = Services.GetRequiredService<EnvironmentVariableService>();
        var windowManagement = Services.GetRequiredService<WindowManagementService>();
        var fileLocks = Services.GetRequiredService<FileLockService>();
        var imageTools = Services.GetRequiredService<ImageProcessingService>();
        var textTools = Services.GetRequiredService<TextProcessingService>();
        var agentService = Services.GetRequiredService<IAgentService>();
        var drawerWindow = new DrawerWindow(settings, shellViewModel, recentItems, quickLaunchItems, quickLaunchActions, homeViewModel, windowMonitor, searchCoordinator, appIndex, commandRegistry, commandExecutor, customCommands, clipboardService, clipboardViewModel, systemDiagnostics, hostsFileService, environmentVariables, windowManagement, fileLocks, imageTools, textTools, agentService);
        drawerWindow.Closed += OnDrawerWindowClosed;
        _drawerWindow = drawerWindow;

        _trayIcon = new TrayIconService(
            GetTrayIconPath(),
            () => drawerWindow.DispatcherQueue.TryEnqueue(drawerWindow.ShowDrawer),
            () => drawerWindow.DispatcherQueue.TryEnqueue(ExitApplication));

        _globalHotkey = new GlobalHotkeyService(
            settings,
            () => drawerWindow.DispatcherQueue.TryEnqueue(drawerWindow.ShowDrawerAndFocusSearch));
        GlobalHotkey = _globalHotkey;
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _globalHotkey?.Dispose();
        _globalHotkey = null;
        GlobalHotkey = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _drawerWindow?.Close();
        Log.CloseAndFlush();
    }

    private void OnDrawerWindowClosed(object sender, WindowEventArgs args)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _globalHotkey?.Dispose();
        _globalHotkey = null;
        GlobalHotkey = null;
        Log.CloseAndFlush();
    }

    private static string GetTrayIconPath()
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    private static void ConfigureLogging()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeKit", "logs");
        System.IO.Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(logDir, "edgekit-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        var dbPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeKit", "edgekit.db");

        services.AddSingleton<ISettingsStore>(_ => new SqliteSettingsStore(dbPath));
        services.AddSingleton<IRecentItemsRepository>(_ => new SqliteRecentItemsRepository(dbPath));
        services.AddSingleton<IQuickLaunchRepository>(_ => new SqliteQuickLaunchRepository(dbPath));
        services.AddSingleton<ICustomCommandRepository>(_ => new SqliteCustomCommandRepository(dbPath));
        services.AddSingleton<IAgentRepository>(_ => new SqliteAgentRepository(dbPath));

        var clipboardImageDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeKit", "clipboard-images");
        services.AddSingleton<EdgeKit.Core.Clipboard.IClipboardRepository>(
            _ => new EdgeKit.Data.Clipboard.SqliteClipboardRepository(dbPath, clipboardImageDir));
        services.AddSingleton<EdgeKit.Core.Clipboard.IClipboardClassifier, EdgeKit.Services.Clipboard.NoopClipboardClassifier>();
        services.AddSingleton(sp => new EdgeKit.Services.Clipboard.ClipboardService(
            sp.GetRequiredService<EdgeKit.Core.Clipboard.IClipboardRepository>(),
            sp.GetRequiredService<EdgeKit.Core.Clipboard.IClipboardClassifier>(),
            clipboardImageDir));
        services.AddSingleton<ClipboardContentWriter>();
        services.AddSingleton<QuickLaunchActionService>();
        services.AddSingleton<SystemDiagnosticsService>();
        services.AddSingleton<HostsFileService>();
        services.AddSingleton<EnvironmentVariableService>();
        services.AddSingleton<WindowManagementService>();
        services.AddSingleton<FileLockService>();
        services.AddSingleton<ImageProcessingService>();
        services.AddSingleton<TextProcessingService>();
        services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        services.AddSingleton<AgentToolRegistry>();
        services.AddSingleton<IAgentToolRegistry>(sp => sp.GetRequiredService<AgentToolRegistry>());
        services.AddSingleton<McpToolService>();
        services.AddSingleton<AgentToolExecutor>();
        services.AddSingleton<IAgentService, AgentService>();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IStartupLaunchService, StartupLaunchService>();
        services.AddSingleton<IToolCatalog, ToolCatalog>();
        services.AddSingleton<ForegroundWindowMonitor>();
        services.AddSingleton<Search.InstalledAppIndex>();
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<CommandExecutor>();
        services.AddSingleton<Search.SearchEngine>();
        services.AddSingleton<Search.EverythingSearchService>();
        services.AddSingleton<Search.SearchCoordinator>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<DrawerShellViewModel>();
        services.AddTransient<ClipboardHistoryViewModel>();

        return services.BuildServiceProvider();
    }

    private static int? TryRunInternalCommand()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3
            && string.Equals(args[1], HostsFileService.ElevatedSaveArgument, StringComparison.OrdinalIgnoreCase))
        {
            return HostsFileService.ExecuteElevatedSaveCommand(args[2]);
        }

        if (args.Length >= 3
            && string.Equals(args[1], EnvironmentVariableService.ElevatedSaveArgument, StringComparison.OrdinalIgnoreCase))
        {
            return EnvironmentVariableService.ExecuteElevatedSaveCommand(args[2]);
        }

        if (args.Length >= 3
            && string.Equals(args[1], FileLockService.ElevatedActionArgument, StringComparison.OrdinalIgnoreCase))
        {
            return FileLockService.ExecuteElevatedActionCommand(args[2]);
        }

        return null;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception: {Message}", e.Message);
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Unhandled domain exception");
        }

        Log.CloseAndFlush();
    }
}
