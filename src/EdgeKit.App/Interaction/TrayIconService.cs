using System;
using System.IO;
using System.Runtime.InteropServices;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 基于 Win32 Shell_NotifyIcon 的系统托盘入口。独立 message-only window 接收托盘消息。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_USER + 0x423;
    private const uint TrayIconId = 1;
    private const uint ShowCommandId = 1001;
    private const uint ExitCommandId = 1002;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _showAction;
    private readonly Action _exitAction;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private nint _messageHwnd;
    private nint _iconHandle;
    private bool _disposed;

    public TrayIconService(string iconPath, Action showAction, Action exitAction)
    {
        _showAction = showAction;
        _exitAction = exitAction;
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
                ShowContextMenu();
                return nint.Zero;
            }

            if (mouseMessage == NativeMethods.WM_LBUTTONDBLCLK)
            {
                _showAction();
                return nint.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, ShowCommandId, "显示 EdgeKit");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, ExitCommandId, "退出");

            NativeMethods.SetForegroundWindow(_messageHwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                0,
                _messageHwnd,
                nint.Zero);
            NativeMethods.PostMessage(_messageHwnd, NativeMethods.WM_NULL, nint.Zero, nint.Zero);

            if (command == ShowCommandId)
            {
                _showAction();
            }
            else if (command == ExitCommandId)
            {
                _exitAction();
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
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
