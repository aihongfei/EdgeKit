using EdgeKit.Services.Diagnostics;

namespace EdgeKit.App.Views;

/// <summary>系统工具页导航参数。</summary>
public sealed record SystemToolsPageParameter(
    string ToolId,
    nint OwnerHwnd,
    SystemDiagnosticsService Diagnostics,
    HostsFileService Hosts,
    EnvironmentVariableService EnvironmentVariables,
    WindowManagementService WindowManagement,
    FileLockService FileLocks);
