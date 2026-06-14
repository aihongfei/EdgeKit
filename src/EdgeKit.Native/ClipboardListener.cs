using System;
using System.Collections.Concurrent;

namespace EdgeKit.Native;

/// <summary>
/// 剪贴板变化监听器。在目标窗口上注册 AddClipboardFormatListener，并子类化窗口过程
/// 拦截 WM_CLIPBOARDUPDATE，剪贴板内容变化时触发 <see cref="Changed"/>。
/// 与 <see cref="WindowBorderRemover"/> 的子类化相互链式兼容（均通过 CallWindowProc 调用前一过程）。
/// 隐藏窗口同样能收到剪贴板消息，无需窗口可见。
/// </summary>
public sealed class ClipboardListener : IDisposable
{
    // 保活委托与旧窗口过程，避免被 GC 回收（按 hwnd 索引，支持多窗口）。
    private static readonly ConcurrentDictionary<nint, ClipboardListener> Listeners = new();

    private nint _hwnd;
    private NativeMethods.WndProcDelegate? _newProc;
    private nint _oldProc;
    private bool _disposed;

    /// <summary>剪贴板内容发生变化时触发（在 UI 线程的窗口过程内）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 在指定窗口上启动监听（幂等：同一窗口只注册一次）。
    /// </summary>
    public void Start(nint hwnd)
    {
        if (hwnd == nint.Zero || _hwnd != nint.Zero)
        {
            return;
        }

        _hwnd = hwnd;
        Listeners[hwnd] = this;

        _newProc = WndProc;
        _oldProc = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_WNDPROC, _newProc);

        NativeMethods.AddClipboardFormatListener(hwnd);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // 监听回调异常不应破坏窗口过程链。
            }
        }

        return NativeMethods.CallWindowProc(_oldProc, hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != nint.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);

            // 还原窗口过程。
            if (_oldProc != nint.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _oldProc);
            }

            Listeners.TryRemove(_hwnd, out _);
            _hwnd = nint.Zero;
        }

        Changed = null;
    }
}