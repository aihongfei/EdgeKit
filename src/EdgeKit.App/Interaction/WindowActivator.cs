using System;
using System.Diagnostics;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 窗口激活器：尝试把某个最近窗口重新带到前台。
/// 解析顺序（逐级兜底）：
/// 1. 会话内缓存句柄仍有效 → 直接激活。
/// 2. 句柄失效 → 按"进程路径 + 标题"枚举查找仍存活的窗口并激活。
/// 3. 仍找不到 → 按"进程路径"枚举该进程的任一主窗口并激活。
/// 4. 进程已退出 → 按可执行文件路径重新拉起。
/// 因记录的标题可能随时间变化（如浏览器标签页），单纯依赖缓存句柄或标题匹配
/// 会导致旧条目点击无反应，故增加按进程枚举的兜底。
/// </summary>
internal static class WindowActivator
{
    /// <summary>
    /// 激活指定窗口；缓存句柄失效时按进程查找存活窗口，仍无果则按路径重新拉起。
    /// </summary>
    /// <param name="hwnd">会话内缓存的窗口句柄（可能已失效或为 0）。</param>
    /// <param name="processPath">该窗口所属进程的可执行文件路径，用于匹配与重新拉起。</param>
    /// <param name="title">记录时的窗口标题，用于优先匹配同进程下的目标窗口。</param>
    /// <returns>是否成功激活或拉起。</returns>
    public static bool Activate(nint hwnd, string processPath, string title)
    {
        // 1. 缓存句柄仍有效，直接激活。
        if (hwnd != nint.Zero && NativeMethods.IsWindow(hwnd) && TryActivateExisting(hwnd))
        {
            return true;
        }

        // 2/3. 按进程查找仍存活的窗口（优先标题匹配）。
        var resolved = FindWindowByProcess(processPath, title);
        if (resolved != nint.Zero && TryActivateExisting(resolved))
        {
            return true;
        }

        // 4. 进程已退出，按路径重新拉起。
        return TryRelaunch(processPath);
    }

    /// <summary>
    /// 枚举所有顶级窗口，查找属于指定进程路径的可见窗口。
    /// 优先返回标题完全匹配的；否则返回该进程的首个合格窗口。
    /// </summary>
    private static nint FindWindowByProcess(string processPath, string title)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return nint.Zero;
        }

        var titleMatch = nint.Zero;
        var processMatch = nint.Zero;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true; // 继续枚举
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            string path;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return true; // 进程不可访问，跳过
            }

            if (!string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 同进程：记录首个合格窗口；若标题也匹配则锁定并停止枚举。
            if (processMatch == nint.Zero)
            {
                processMatch = hWnd;
            }

            if (!string.IsNullOrEmpty(title)
                && string.Equals(WindowQuery.GetTitle(hWnd), title, StringComparison.Ordinal))
            {
                titleMatch = hWnd;
                return false; // 找到精确匹配，停止枚举
            }

            return true;
        }, nint.Zero);

        return titleMatch != nint.Zero ? titleMatch : processMatch;
    }

    private static bool TryActivateExisting(nint hwnd)
    {
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd))
            {
                // 窗口既不可见也未最小化，多半已关闭。
                return false;
            }

            // 最小化的窗口先还原。
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            // 前台锁定绕过：把当前线程输入附加到目标窗口线程，再 SetForegroundWindow。
            var foreground = NativeMethods.GetForegroundWindow();
            var currentThread = NativeMethods.GetCurrentThreadId();
            var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out var targetPid);
            var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);

            NativeMethods.AllowSetForegroundWindow(targetPid);

            var attached = false;
            if (foregroundThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            var ok = NativeMethods.SetForegroundWindow(hwnd);

            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "激活已有窗口失败 hwnd={Hwnd}", hwnd);
            return false;
        }
    }

    private static bool TryRelaunch(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "按路径重新拉起窗口失败 path={Path}", processPath);
            return false;
        }
    }
}