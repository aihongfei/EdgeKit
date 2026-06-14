using System;
using System.Runtime.InteropServices;
using System.Threading;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 通过 Raw Input 读取原始鼠标横向位移，用于"推墙"手势。
///
/// 当光标已被系统钳制在屏幕物理最左边缘后，<see cref="NativeMethods.GetCursorPos"/>
/// 返回的 X 不再变化，无法度量用户"继续向左推"的力度。Raw Input（WM_INPUT）
/// 提供的是设备级相对位移（lLastX），即使光标被钳制仍持续上报，因此可用它累积推力。
///
/// 实现：创建一个 message-only 窗口并注册鼠标 Raw Input（RIDEV_INPUTSINK 使其在
/// 非前台时也能收到事件），在 WndProc 处理 WM_INPUT 累积向左（lLastX &lt; 0）的位移。
/// 调用方周期性用 <see cref="ConsumeLeftPush"/> 取走并清零累积值。
/// </summary>
public sealed class RawMousePushMonitor : IDisposable
{
    private static readonly nint HwndMessage = new(-3);

    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private nint _messageHwnd;
    private bool _registered;
    private bool _active;
    private bool _disposed;

    // 累积的"向左推"位移（正值，单位为原始设备计数）。WndProc 与消费线程共享，用 Interlocked 保护。
    private long _accumulatedLeft;

    private readonly uint _rawHeaderSize =
        (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();

    public RawMousePushMonitor()
    {
        _wndProc = WndProc;
        _className = "EdgeKitRawMouseWindow_" + Guid.NewGuid().ToString("N");
        CreateMessageWindow();
    }

    private void CreateMessageWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = _className
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("注册 Raw Input 消息窗口失败。");
        }

        _messageHwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "EdgeKit RawMouse",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            nint.Zero,
            instance,
            nint.Zero);

        if (_messageHwnd == nint.Zero)
        {
            throw new InvalidOperationException("创建 Raw Input 消息窗口失败。");
        }
    }

    /// <summary>开始累积推力。进入"蓄力"态时调用；注册鼠标 Raw Input 并清零累积。</summary>
    public void Start()
    {
        if (_disposed || _active)
        {
            return;
        }

        Interlocked.Exchange(ref _accumulatedLeft, 0);

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = _messageHwnd
            }
        };

        _registered = NativeMethods.RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

        if (!_registered)
        {
            Log.Warning(
                "注册鼠标 Raw Input 失败 error={Error}",
                Marshal.GetLastPInvokeError());
            return;
        }

        _active = true;
    }

    /// <summary>停止累积推力。离开"蓄力"态时调用；注销 Raw Input 并清零累积。</summary>
    public void Stop()
    {
        if (!_active)
        {
            Interlocked.Exchange(ref _accumulatedLeft, 0);
            return;
        }

        _active = false;
        Interlocked.Exchange(ref _accumulatedLeft, 0);

        if (!_registered)
        {
            return;
        }

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
                dwFlags = NativeMethods.RIDEV_REMOVE,
                hwndTarget = nint.Zero
            }
        };

        NativeMethods.RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());

        _registered = false;
    }

    /// <summary>取走并清零自上次调用以来累积的"向左推"位移（≥0）。</summary>
    public int ConsumeLeftPush()
    {
        var value = Interlocked.Exchange(ref _accumulatedLeft, 0);
        if (value < 0)
        {
            value = 0;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_INPUT && _active)
        {
            AccumulateFromRawInput(lParam);
            // WM_INPUT 仍需交给 DefWindowProc 做清理。
            return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void AccumulateFromRawInput(nint lParam)
    {
        uint size = 0;
        // 先查询所需缓冲区大小。
        if (NativeMethods.GetRawInputData(
                lParam,
                NativeMethods.RID_INPUT,
                nint.Zero,
                ref size,
                _rawHeaderSize) != 0 || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = NativeMethods.GetRawInputData(
                lParam,
                NativeMethods.RID_INPUT,
                buffer,
                ref size,
                _rawHeaderSize);

            if (read != size)
            {
                return;
            }

            var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUTMOUSE>(buffer);
            if (raw.header.dwType != NativeMethods.RIM_TYPEMOUSE)
            {
                return;
            }

            // 仅相对移动模式下 lLastX 才是位移增量；绝对模式（少见，如远程桌面/数位板）忽略。
            if ((raw.mouse.usFlags & NativeMethods.MOUSE_MOVE_ABSOLUTE) != 0)
            {
                return;
            }

            // 只累积向左推（lLastX < 0）的量。
            if (raw.mouse.lLastX < 0)
            {
                Interlocked.Add(ref _accumulatedLeft, -raw.mouse.lLastX);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_messageHwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_messageHwnd);
            _messageHwnd = nint.Zero;
        }
    }
}