using EdgeKit.Core.Services;
using Microsoft.Win32;

namespace EdgeKit.Services.Settings;

/// <summary>通过当前用户 Run 注册表项管理非打包桌面应用的开机自启。</summary>
public sealed class StartupLaunchService : IStartupLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgeKit";
    private const string StartupArgument = "--startup";
    private const string ExeName = "EdgeKit.App.exe";

    public StartupLaunchStatus GetStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(ValueName) as string;
            return new StartupLaunchStatus(!string.IsNullOrWhiteSpace(command), command);
        }
        catch
        {
            return new StartupLaunchStatus(false, null);
        }
    }

    public StartupLaunchResult SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                {
                    return new StartupLaunchResult(false, "无法打开当前用户启动项注册表。");
                }

                key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
                return new StartupLaunchResult(true, null);
            }

            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return new StartupLaunchResult(true, null);
        }
        catch (Exception ex)
        {
            return new StartupLaunchResult(false, ex.Message);
        }
    }

    public void RefreshCommandIfEnabled()
    {
        if (GetStatus().IsEnabled)
        {
            SetEnabled(enabled: true);
        }
    }

    private static string BuildStartupCommand()
        => $"\"{GetExecutablePath()}\" {StartupArgument}";

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        return Path.Combine(AppContext.BaseDirectory, ExeName);
    }
}
