using System.Collections.Concurrent;

namespace EdgeKit.Native;

/// <summary>
/// 通过子类化窗口、拦截 WM_NCCALCSIZE 去掉非客户区边框，
/// 彻底消除无边框窗口在某些 DPI/主题下残留的白色边框线。
/// 处理 WM_NCCALCSIZE 时返回 0 并保留客户区为整个窗口矩形。
/// </summary>
public static class WindowBorderRemover
{
    // 保活委托与旧窗口过程，避免被 GC 回收。
    private static readonly ConcurrentDictionary<nint, Holder> Holders = new();

    private sealed class Holder
    {
        public NativeMethods.WndProcDelegate? NewProc;
        public nint OldProc;
    }

    /// <summary>对指定窗口应用去边框（幂等：同一窗口只子类化一次）。</summary>
    public static void Apply(nint hwnd)
    {
        if (hwnd == nint.Zero || Holders.ContainsKey(hwnd))
        {
            return;
        }

        var holder = new Holder();
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

        return NativeMethods.CallWindowProc(holder.OldProc, hwnd, msg, wParam, lParam);
    }
}