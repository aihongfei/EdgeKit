using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Serilog;

namespace EdgeKit.App.Search;

/// <summary>
/// 启动本机已安装应用。AppsFolder 的解析名分两种：
/// 1) AUMID / ProgID（UWP、商店、已注册程序，如 <c>Anysphere.Cursor</c>）→
///    通过 Shell.Application 找到 AppsFolder 子项并执行默认打开动词；
/// 2) 完整可执行文件路径（部分 Win32 桌面程序，如 <c>C:\...\idea64.exe</c>）→
///    直接 <see cref="Process.Start(ProcessStartInfo)"/> 启动该文件。
/// </summary>
internal static class InstalledAppLauncher
{
    public static bool Launch(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        try
        {
            if (LooksLikeFilePath(appUserModelId))
            {
                // Win32 桌面程序：解析名即可执行文件全路径，直接启动。
                Log.Information("启动应用(文件路径) path={Path}", appUserModelId);
                Process.Start(new ProcessStartInfo
                {
                    FileName = appUserModelId,
                    UseShellExecute = true
                });
                return true;
            }

            // AUMID / UWP / ProgID：从 AppsFolder 找到对应 Shell 项并执行默认打开动词。
            if (TryLaunchAppsFolderItem(appUserModelId))
            {
                Log.Information("启动应用(AppsFolder COM) id={Id}", appUserModelId);
                return true;
            }

            Log.Warning("未能通过 AppsFolder 启动应用 id={Id}", appUserModelId);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动应用失败 id={Id}", appUserModelId);
            return false;
        }
    }

    /// <summary>
    /// 判断解析名是否为完整文件路径（如 <c>C:\dir\app.exe</c>）。
    /// AUMID / ProgID 不含驱动器盘符冒号，据此区分。
    /// </summary>
    private static bool LooksLikeFilePath(string id)
    {
        // 形如 X:\... 或 X:/...
        if (id.Length > 2 && char.IsLetter(id[0]) && id[1] == ':' &&
            (id[2] == '\\' || id[2] == '/'))
        {
            return true;
        }

        // UNC 路径 \\server\share
        return id.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool TryLaunchAppsFolderItem(string appUserModelId)
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
                    args: new object[] { "open", string.Empty });
            }
            catch (MissingMethodException)
            {
                itemType.InvokeMember(
                    "InvokeVerb",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: item,
                    args: new object[] { "open" });
            }

            return true;
        }
        catch (TargetInvocationException ex)
        {
            Log.Debug(ex.InnerException ?? ex, "AppsFolder COM 启动失败 id={Id}", appUserModelId);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AppsFolder COM 启动失败 id={Id}", appUserModelId);
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
}
