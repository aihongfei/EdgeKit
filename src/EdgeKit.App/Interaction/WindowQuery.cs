using System;
using System.Diagnostics;
using System.Text;
using EdgeKit.Native;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 从窗口句柄提取标题、进程名与进程可执行文件路径的工具方法，并判断窗口是否应被记录。
/// </summary>
internal static class WindowQuery
{
    /// <summary>提取窗口标题，失败返回空串。</summary>
    public static string GetTitle(nint hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>提取窗口所属进程的名称与可执行文件路径。任一失败返回空串。</summary>
    public static (string ProcessName, string ProcessPath) GetProcess(nint hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return (string.Empty, string.Empty);
            }

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            string path = string.Empty;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // 访问受保护进程的 MainModule 可能抛异常，忽略，仅用进程名。
            }

            return (name, path);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// 判断窗口是否为应记录的"应用窗口"：可见、非工具窗口、非 cloaked、标题非空。
    /// </summary>
    public static bool IsEligibleWindow(nint hwnd)
    {
        if (hwnd == nint.Zero || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        // 排除工具窗口（WS_EX_TOOLWINDOW），它们不出现在 Alt+Tab 列表。
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        // 排除 DWM cloaked（隐藏的 UWP 外壳窗口）。
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(GetTitle(hwnd));
    }
}