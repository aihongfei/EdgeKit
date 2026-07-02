using System;
using System.IO;
using System.Runtime.InteropServices;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 基于 Win32 Shell_NotifyIcon 的系统托盘入口。独立 message-only window 接收托盘消息。
/// 右键菜单现在由调用方通过 <see cref="ShowMenuAction"/> 自行渲染（XAML 弹出窗口）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_USER + 0x423;
    private const uint TrayIconId = 1;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _defaultAction;
    private readonly Action _showMenuAction;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private nint _messageHwnd;
    private nint _iconHandle;
    private bool _disposed;

    public TrayIconService(string iconPath, Action defaultAction, Action showMenuAction)
    {
        _defaultAction = defaultAction;
        _showMenuAction = showMenuAction;
        _wndProc = WndProc;
        _className = "EdgeKitTrayWindow_" + Guid.NewGuid().ToString("N");

        CreateMessageWindow();
        AddTrayIcon(iconPath);
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
            throw new InvalidOperationException("注册托盘消息窗口失败。");
        }

        _messageHwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "EdgeKit Tray",
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
            throw new InvalidOperationException("创建托盘消息窗口失败。");
        }
    }

    private void AddTrayIcon(string iconPath)
    {
        if (File.Exists(iconPath))
        {
            _iconHandle = NativeMethods.LoadImage(
                nint.Zero,
                iconPath,
                NativeMethods.IMAGE_ICON,
                0,
                0,
                NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
        }

        var data = CreateNotifyData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.hIcon = _iconHandle;
        data.szTip = "EdgeKit";

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            Log.Warning("添加系统托盘图标失败");
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyData()
        => new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageHwnd,
            uID = TrayIconId,
            uCallbackMessage = TrayCallbackMessage,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == NativeMethods.WM_RBUTTONUP)
            {
                // 将托盘消息窗口设为前台，以保留本进程调用 SetForegroundWindow 的权限，
                // 否则后续弹出的 XAML 菜单可能无法真正获得焦点，导致失焦关闭失效。
                NativeMethods.SetForegroundWindow(_messageHwnd);
                _showMenuAction();
                return nint.Zero;
            }

            if (mouseMessage == NativeMethods.WM_LBUTTONDBLCLK)
            {
                _defaultAction();
                return nint.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var data = CreateNotifyData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);

        if (_iconHandle != nint.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }

        if (_messageHwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_messageHwnd);
            _messageHwnd = nint.Zero;
        }
    }
}