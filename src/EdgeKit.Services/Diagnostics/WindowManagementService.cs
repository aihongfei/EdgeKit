using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EdgeKit.Native;

namespace EdgeKit.Services.Diagnostics;

/// <summary>Enumerates and adjusts regular application windows.</summary>
public sealed class WindowManagementService
{
    private readonly Dictionary<nint, WindowOriginalState> _originalStates = new();

    public WindowManagementSnapshot ReadSnapshot(nint ownerHwnd)
    {
        var windows = new List<ManagedWindowEntry>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (TryReadWindow(hwnd, ownerHwnd, out var entry))
            {
                windows.Add(entry);
            }

            return true;
        }, nint.Zero);

        return new WindowManagementSnapshot(
            DateTime.Now,
            windows
                .OrderBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    public ManagedWindowEntry? ReadWindow(nint hwnd, nint ownerHwnd = 0)
        => TryReadWindow(hwnd, ownerHwnd, out var entry) ? entry : null;

    public ToolActionResult SetTopMost(ManagedWindowEntry entry, bool enabled)
    {
        if (!EnsureWindow(entry.Hwnd, out var error))
        {
            return error;
        }

        CaptureOriginal(entry.Hwnd);
        var ok = NativeMethods.SetWindowPos(
            entry.Hwnd,
            enabled ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        return ok
            ? new ToolActionResult(true, enabled ? "窗口已置顶" : "窗口已取消置顶")
            : new ToolActionResult(false, "置顶设置失败");
    }

    public ToolActionResult SetOpacity(ManagedWindowEntry entry, int opacityPercent)
    {
        if (!EnsureWindow(entry.Hwnd, out var error))
        {
            return error;
        }

        opacityPercent = Math.Clamp(opacityPercent, 35, 100);
        CaptureOriginal(entry.Hwnd);

        var exStyle = NativeMethods.GetWindowLong(entry.Hwnd, NativeMethods.GWL_EXSTYLE);
        if (opacityPercent >= 100)
        {
            if (_originalStates.TryGetValue(entry.Hwnd, out var original) && !original.WasLayered)
            {
                _ = NativeMethods.SetWindowLongPtr(
                    entry.Hwnd,
                    NativeMethods.GWL_EXSTYLE,
                    new nint(exStyle & ~NativeMethods.WS_EX_LAYERED));
                return new ToolActionResult(true, "透明度已恢复为 100%");
            }

            if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            {
                _ = NativeMethods.SetWindowLongPtr(
                    entry.Hwnd,
                    NativeMethods.GWL_EXSTYLE,
                    new nint(exStyle | NativeMethods.WS_EX_LAYERED));
            }

            return NativeMethods.SetLayeredWindowAttributes(entry.Hwnd, 0, 255, NativeMethods.LWA_ALPHA)
                ? new ToolActionResult(true, "透明度已设为 100%")
                : new ToolActionResult(false, "透明度设置失败");
        }

        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            _ = NativeMethods.SetWindowLongPtr(
                entry.Hwnd,
                NativeMethods.GWL_EXSTYLE,
                new nint(exStyle | NativeMethods.WS_EX_LAYERED));
        }

        var alpha = (byte)Math.Round(opacityPercent / 100d * 255d);
        return NativeMethods.SetLayeredWindowAttributes(entry.Hwnd, 0, alpha, NativeMethods.LWA_ALPHA)
            ? new ToolActionResult(true, $"透明度已设为 {opacityPercent}%")
            : new ToolActionResult(false, "透明度设置失败");
    }

    public ToolActionResult RestoreWindow(ManagedWindowEntry entry)
    {
        if (!EnsureWindow(entry.Hwnd, out var error))
        {
            return error;
        }

        if (!_originalStates.TryGetValue(entry.Hwnd, out var original))
        {
            return new ToolActionResult(false, "没有可还原的本次会话记录");
        }

        _ = NativeMethods.SetWindowLongPtr(
            entry.Hwnd,
            NativeMethods.GWL_EXSTYLE,
            new nint(original.ExStyle));

        if (original.WasLayered && original.HasLayeredAlpha)
        {
            _ = NativeMethods.SetLayeredWindowAttributes(
                entry.Hwnd,
                original.ColorKey,
                original.Alpha,
                original.LayeredFlags);
        }

        _ = NativeMethods.SetWindowPos(
            entry.Hwnd,
            original.WasTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _originalStates.Remove(entry.Hwnd);
        return new ToolActionResult(true, "窗口状态已还原");
    }

    public ToolActionResult ActivateWindow(ManagedWindowEntry entry)
    {
        if (!EnsureWindow(entry.Hwnd, out var error))
        {
            return error;
        }

        try
        {
            if (NativeMethods.IsIconic(entry.Hwnd))
            {
                NativeMethods.ShowWindow(entry.Hwnd, NativeMethods.SW_RESTORE);
            }

            NativeMethods.AllowSetForegroundWindow((uint)Math.Max(entry.ProcessId, 0));
            return NativeMethods.SetForegroundWindow(entry.Hwnd)
                ? new ToolActionResult(true, "窗口已激活")
                : new ToolActionResult(false, "窗口激活失败");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ToolActionResult(false, "窗口激活失败：" + ex.Message);
        }
    }

    public ToolActionResult OpenProcessDirectory(ManagedWindowEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProcessDirectory) || !Directory.Exists(entry.ProcessDirectory))
        {
            return new ToolActionResult(false, "进程目录不可用");
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.ProcessDirectory)
            {
                UseShellExecute = true
            });
            return new ToolActionResult(true, "已打开进程目录");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new ToolActionResult(false, "打开失败：" + ex.Message);
        }
    }

    public string BuildWindowReport(ManagedWindowEntry entry)
        => string.Join(Environment.NewLine, new[]
        {
            "EdgeKit 窗口信息",
            $"标题: {entry.Title}",
            $"进程: {entry.ProcessName}",
            $"PID: {entry.ProcessIdText}",
            $"路径: {entry.ProcessPath}",
            $"句柄: {entry.HwndHex}",
            $"矩形: {entry.RectText}",
            $"置顶: {entry.TopMostText}",
            $"透明度: {entry.OpacityText}"
        });

    private void CaptureOriginal(nint hwnd)
    {
        if (_originalStates.ContainsKey(hwnd))
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var wasLayered = (exStyle & NativeMethods.WS_EX_LAYERED) != 0;
        var hasLayeredAlpha = false;
        var colorKey = 0u;
        var alpha = (byte)255;
        var flags = 0u;

        if (wasLayered
            && NativeMethods.GetLayeredWindowAttributes(hwnd, out colorKey, out var currentAlpha, out flags))
        {
            hasLayeredAlpha = (flags & NativeMethods.LWA_ALPHA) != 0;
            alpha = currentAlpha;
        }

        _originalStates[hwnd] = new WindowOriginalState(
            exStyle,
            (exStyle & NativeMethods.WS_EX_TOPMOST) != 0,
            wasLayered,
            hasLayeredAlpha,
            colorKey,
            alpha,
            flags);
    }

    private static bool TryReadWindow(nint hwnd, nint ownerHwnd, out ManagedWindowEntry entry)
    {
        entry = default!;

        if (hwnd == nint.Zero
            || hwnd == ownerHwnd
            || hwnd == NativeMethods.GetDesktopWindow()
            || hwnd == NativeMethods.GetShellWindow()
            || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        var title = GetTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CLOAKED,
                out var cloaked,
                sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
        {
            return false;
        }

        var (processName, processPath) = GetProcess((int)pid);
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            rect = new NativeMethods.RECT();
        }

        entry = new ManagedWindowEntry(
            hwnd,
            title,
            processName,
            processPath,
            unchecked((int)pid),
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            (exStyle & NativeMethods.WS_EX_TOPMOST) != 0,
            ReadOpacityPercent(hwnd, exStyle));
        return true;
    }

    private static int ReadOpacityPercent(nint hwnd, int exStyle)
    {
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0
            && NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
            && (flags & NativeMethods.LWA_ALPHA) != 0)
        {
            return Math.Clamp((int)Math.Round(alpha / 255d * 100d), 1, 100);
        }

        return 100;
    }

    private static string GetTitle(nint hwnd)
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

    private static (string ProcessName, string ProcessPath) GetProcess(int processId)
    {
        if (processId <= 0)
        {
            return ("不可用", string.Empty);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            var path = string.Empty;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // Protected processes can deny MainModule access.
            }

            return (name, path);
        }
        catch
        {
            return ("不可用", string.Empty);
        }
    }

    private static bool EnsureWindow(nint hwnd, out ToolActionResult error)
    {
        if (hwnd == nint.Zero || !NativeMethods.IsWindow(hwnd))
        {
            error = new ToolActionResult(false, "窗口已关闭或不可用");
            return false;
        }

        error = new ToolActionResult(true, string.Empty);
        return true;
    }

    private sealed record WindowOriginalState(
        int ExStyle,
        bool WasTopMost,
        bool WasLayered,
        bool HasLayeredAlpha,
        uint ColorKey,
        byte Alpha,
        uint LayeredFlags);
}

public sealed record WindowManagementSnapshot(
    DateTime RefreshedAt,
    IReadOnlyList<ManagedWindowEntry> Windows)
{
    public string SummaryText => $"{Windows.Count} 个窗口 · {RefreshedAt:HH:mm:ss}";
}

public sealed record ManagedWindowEntry(
    nint Hwnd,
    string Title,
    string ProcessName,
    string ProcessPath,
    int ProcessId,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool IsTopMost,
    int OpacityPercent)
{
    public string HwndHex => "0x" + Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    public string ProcessIdText => ProcessId <= 0 ? "不可用" : ProcessId.ToString(CultureInfo.InvariantCulture);

    public string ProcessDirectory
        => string.IsNullOrWhiteSpace(ProcessPath) ? string.Empty : Path.GetDirectoryName(ProcessPath) ?? string.Empty;

    public string RectText => $"{Left},{Top} - {Right},{Bottom} ({Math.Max(Right - Left, 0)}x{Math.Max(Bottom - Top, 0)})";

    public string TopMostText => IsTopMost ? "是" : "否";

    public string OpacityText => $"{OpacityPercent}%";

    public string Detail => $"{ProcessName} · PID {ProcessIdText} · {HwndHex}";

    public string StateText => $"置顶 {TopMostText} · 透明度 {OpacityText}";

    public bool CanOpenDirectory => !string.IsNullOrWhiteSpace(ProcessDirectory);
}
