using System.Collections.Concurrent;

namespace EdgeKit.Native;

/// <summary>
/// 通过子类化窗口、拦截 WM_NCCALCSIZE 去掉非客户区边框，
/// 彻底消除无边框窗口在某些 DPI/主题下残留的白色边框线。
/// 处理 WM_NCCALCSIZE 时返回 0 并保留客户区为整个窗口矩形。
/// 可选启用 WM_NCHITTEST，使无边框窗口仍能拖动四边/四角调整大小。
/// </summary>
public static class WindowBorderRemover
{
    // 保活委托与旧窗口过程，避免被 GC 回收。
    private static readonly ConcurrentDictionary<nint, Holder> Holders = new();

    private sealed class Holder
    {
        public NativeMethods.WndProcDelegate? NewProc;
        public nint OldProc;
        public bool EnableResize;
    }

    /// <summary>对指定窗口应用去边框（幂等：同一窗口只子类化一次）。</summary>
    public static void Apply(nint hwnd, bool enableResize = false)
    {
        if (hwnd == nint.Zero || Holders.ContainsKey(hwnd))
        {
            return;
        }

        var holder = new Holder { EnableResize = enableResize };
        holder.NewProc = (h, msg, w, l) => WndProc(holder, h, msg, w, l);

        holder.OldProc = NativeMethods.SetWindowLongPtr(
            hwnd, NativeMethods.GWLP_WNDPROC, holder.NewProc);

        Holders[hwnd] = holder;

        // 触发非客户区重算，使新边框策略立即生效。
        NativeMethods.SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    private static nint WndProc(Holder holder, nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_NCCALCSIZE && wParam != nint.Zero)
        {
            // wParam=TRUE 时返回 0，表示客户区占满整个窗口，无非客户区边框。
            return nint.Zero;
        }

        if (holder.EnableResize && msg == NativeMethods.WM_NCHITTEST)
        {
            var hit = HitTestResizeBorder(hwnd, lParam);
            if (hit != 0)
            {
                return new nint(hit);
            }
        }

        return NativeMethods.CallWindowProc(holder.OldProc, hwnd, msg, wParam, lParam);
    }

    private static int HitTestResizeBorder(nint hwnd, nint lParam)
    {
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return 0;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var margin = (int)(8 * dpi / 96.0);

        var left = x < rect.Left + margin;
        var right = x >= rect.Right - margin;
        var top = y < rect.Top + margin;
        var bottom = y >= rect.Bottom - margin;

        if (top && left) { return NativeMethods.HTTOPLEFT; }
        if (top && right) { return NativeMethods.HTTOPRIGHT; }
        if (bottom && left) { return NativeMethods.HTBOTTOMLEFT; }
        if (bottom && right) { return NativeMethods.HTBOTTOMRIGHT; }
        if (top) { return NativeMethods.HTTOP; }
        if (bottom) { return NativeMethods.HTBOTTOM; }
        if (left) { return NativeMethods.HTLEFT; }
        if (right) { return NativeMethods.HTRIGHT; }

        return 0;
    }
}