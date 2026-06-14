using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using EdgeKit.App.Search;
using EdgeKit.Core.QuickLaunch;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 快速启动与搜索右键菜单共用的目标操作：打开、打开所在位置、管理员方式打开。
/// </summary>
public sealed class QuickLaunchActionService
{
    public bool Launch(QuickLaunchItem item)
    {
        if (item.Kind == QuickLaunchItemKind.App)
        {
            return InstalledAppLauncher.Launch(item.Target);
        }

        return LaunchPath(item.Target);
    }

    public bool LaunchAppTarget(string target) => InstalledAppLauncher.Launch(target);

    public bool OpenLocation(QuickLaunchItem item) => OpenLocation(item.Kind, item.Target);

    public bool OpenLocation(QuickLaunchItemKind kind, string target)
    {
        if (!CanOpenLocation(kind, target))
        {
            return false;
        }

        if (kind == QuickLaunchItemKind.App && !IsFileSystemPath(target))
        {
            return OpenAppsFolderLocation(target);
        }

        try
        {
            return OpenFileSystemLocation(NormalizePath(target));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开所在位置失败 target={Target}", target);
            return false;
        }
    }

    public bool RunAsAdmin(QuickLaunchItem item) => RunAsAdmin(item.Target);

    public bool RunAsAdmin(string target)
    {
        if (!CanRunAsAdmin(target))
        {
            return false;
        }

        // 文件路径 → Process.Start + runas
        if (IsFileSystemPath(target))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = NormalizePath(target),
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "管理员方式打开失败 target={Target}", target);
                return false;
            }
        }

        // AUMID → COM runas 动词
        if (TryInvokeAppsFolderVerb(target, "runas"))
        {
            return true;
        }

        // fallback → 解析真实路径再提权
        if (TryGetAppsFolderItemPath(target, out var resolvedPath)
            && IsExecutablePath(resolvedPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = NormalizePath(resolvedPath),
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "管理员方式打开(解析路径)失败 target={Target}", target);
            }
        }

        // 最终 fallback → 普通启动（无法提权，至少能打开）
        return InstalledAppLauncher.Launch(target);
    }

    public bool CanOpenLocation(QuickLaunchItemKind kind, string target)
    {
        if (kind == QuickLaunchItemKind.App && !IsFileSystemPath(target))
        {
            return !string.IsNullOrWhiteSpace(target);
        }

        return IsFileSystemPath(target) && (File.Exists(target) || Directory.Exists(target));
    }

    public bool CanRunAsAdmin(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        // AUMID / 非文件路径目标 → 允许尝试 COM runas 动词
        if (!IsFileSystemPath(target))
        {
            return true;
        }

        return IsExecutablePath(target) && File.Exists(target);
    }

    public static bool IsFileSystemPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (target.Length > 2 && char.IsLetter(target[0]) && target[1] == ':' &&
            (target[2] == '\\' || target[2] == '/'))
        {
            return true;
        }

        return target.StartsWith(@"\\", StringComparison.Ordinal);
    }

    public static bool IsExecutablePath(string target)
    {
        if (!IsFileSystemPath(target))
        {
            return false;
        }

        var extension = Path.GetExtension(target);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LaunchPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NormalizePath(target),
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开快速启动目标失败 target={Target}", target);
            return false;
        }
    }

    private static bool OpenAppsFolderLocation(string appUserModelId)
    {
        // 1. 尝试解析真实文件系统路径并定位
        if (TryGetAppsFolderItemPath(appUserModelId, out var path) && OpenFileSystemLocation(path))
        {
            return true;
        }

        // 2. 用 explorer /select 定位 AppsFolder 中的项（Windows 原生支持）
        try
        {
            var selectArgs = $"/select,\"shell:AppsFolder\\{appUserModelId}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = selectArgs,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "explorer /select 定位失败 id={Id}", appUserModelId);
        }

        // 3. COM 动词回退
        if (TryInvokeAppsFolderVerb(appUserModelId, "open file location"))
        {
            return true;
        }

        // 4. 最终 fallback：打开整个 AppsFolder
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开 AppsFolder 位置失败 id={Id}", appUserModelId);
            return false;
        }
    }

    private static bool OpenFileSystemLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = NormalizePath(path);
        var arguments = Directory.Exists(normalized)
            ? $"\"{normalized}\""
            : $"/select,\"{normalized}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true
        });
        return true;
    }

    private static bool TryGetAppsFolderItemPath(string appUserModelId, out string path)
    {
        path = string.Empty;
        object? shell = null;
        object? appsFolder = null;
        object? item = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            appsFolder = shellType.InvokeMember(
                "Namespace",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { "shell:AppsFolder" });

            if (appsFolder is null)
            {
                return false;
            }

            item = appsFolder.GetType().InvokeMember(
                "ParseName",
                BindingFlags.InvokeMethod,
                binder: null,
                target: appsFolder,
                args: new object[] { appUserModelId });

            if (item is null)
            {
                return false;
            }

            var itemType = item.GetType();
            path = (string?)itemType.InvokeMember(
                "Path",
                BindingFlags.GetProperty,
                binder: null,
                target: item,
                args: Array.Empty<object>()) ?? string.Empty;

            return IsFileSystemPath(path) && (File.Exists(path) || Directory.Exists(path));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AppsFolder 路径解析失败 id={Id}", appUserModelId);
            return false;
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(appsFolder);
            ReleaseComObject(shell);
        }
    }

    private static bool TryInvokeAppsFolderVerb(string appUserModelId, string verb)
    {
        object? shell = null;
        object? appsFolder = null;
        object? item = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            appsFolder = shellType.InvokeMember(
                "Namespace",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { "shell:AppsFolder" });

            if (appsFolder is null)
            {
                return false;
            }

            item = appsFolder.GetType().InvokeMember(
                "ParseName",
                BindingFlags.InvokeMethod,
                binder: null,
                target: appsFolder,
                args: new object[] { appUserModelId });

            if (item is null)
            {
                return false;
            }

            var itemType = item.GetType();
            try
            {
                itemType.InvokeMember(
                    "InvokeVerbEx",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: item,
                    args: new object[] { verb, string.Empty });
            }
            catch (MissingMethodException)
            {
                itemType.InvokeMember(
                    "InvokeVerb",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: item,
                    args: new object[] { verb });
            }

            return true;
        }
        catch (TargetInvocationException ex)
        {
            Log.Debug(ex.InnerException ?? ex, "AppsFolder 动词执行失败 id={Id} verb={Verb}", appUserModelId, verb);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AppsFolder 动词执行失败 id={Id} verb={Verb}", appUserModelId, verb);
            return false;
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(appsFolder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static string NormalizePath(string path)
        => path.Trim().Trim('"');
}
