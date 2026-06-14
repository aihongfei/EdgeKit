using System.Text;

namespace EdgeKit.Native;

/// <summary>
/// 基于 <see cref="NativeMethods"/> 的高层封装，提供抽屉交互直接可用的查询能力：
/// 当前光标位置、光标所在显示器工作区、前台窗口是否处于全屏。
/// </summary>
public static class ScreenInterop
{
    /// <summary>获取当前光标的屏幕坐标（物理像素）。失败返回 (0,0)。</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        return NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    }

    /// <summary>获取光标所在显示器的工作区（排除任务栏），失败时回退到主显示器。</summary>
    public static NativeMethods.RECT GetWorkAreaAtCursor()
    {
        NativeMethods.GetCursorPos(out var pt);
        var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return GetWorkArea(monitor);
    }

    private static NativeMethods.RECT GetWorkArea(nint monitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal_SizeOf() };
        if (NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        return new NativeMethods.RECT();
    }

    /// <summary>
    /// 去掉窗口的 DWM 边框线（无边框窗口在 Win11 仍会残留 1px 边框色），
    /// 并保持窗口圆角。用于消除抽屉四周的白边。
    /// </summary>
    public static void RemoveWindowBorder(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        var none = NativeMethods.DWMWA_COLOR_NONE;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref none, sizeof(uint));

        var corner = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private static int Marshal_SizeOf()
        => System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>();

    /// <summary>
    /// 判断当前前台窗口是否处于全屏状态（占满整个显示器，且不是桌面/Shell 本身）。
    /// 用于全屏应用（游戏、视频、演示）中禁用抽屉触发。
    /// </summary>
    public static bool IsForegroundWindowFullscreen()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero)
        {
            return false;
        }

        // 桌面与任务栏本身不算全屏应用。
        if (hwnd == NativeMethods.GetDesktopWindow() || hwnd == NativeMethods.GetShellWindow())
        {
            return false;
        }

        var cls = new StringBuilder(256);
        if (NativeMethods.GetClassName(hwnd, cls, cls.Capacity) > 0)
        {
            var name = cls.ToString();
            // 桌面工作区窗口类，避免把"点击桌面"误判为全屏。
            if (name is "WorkerW" or "Progman")
            {
                return false;
            }
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var wr))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal_SizeOf() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var mon = info.rcMonitor;

        // 窗口矩形完全覆盖（或超出）显示器物理范围即视为全屏。
        return wr.Left <= mon.Left && wr.Top <= mon.Top
            && wr.Right >= mon.Right && wr.Bottom >= mon.Bottom;
    }
}