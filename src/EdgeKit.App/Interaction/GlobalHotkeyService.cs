using System;
using System.Runtime.InteropServices;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Interaction;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private static readonly nint HwndMessage = new(-3);

    private readonly ISettingsService _settings;
    private readonly Action _action;
    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private nint _messageHwnd;
    private string? _registeredText;
    private bool _registered;
    private bool _suspended;
    private bool _disposed;

    public GlobalHotkeyService(ISettingsService settings, Action action)
    {
        _settings = settings;
        _action = action;
        _wndProc = WndProc;
        _className = "EdgeKitHotkeyWindow_" + Guid.NewGuid().ToString("N");

        CreateMessageWindow();
        RegisterFromSettings();
        _settings.Changed += OnSettingsChanged;
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
            throw new InvalidOperationException("注册全局快捷键消息窗口失败。");
        }

        _messageHwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "EdgeKit Hotkey",
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
            throw new InvalidOperationException("创建全局快捷键消息窗口失败。");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RegisterFromSettings();
    }

    private void RegisterFromSettings()
    {
        if (_suspended)
        {
            UnregisterCurrent();
            _registeredText = null;
            return;
        }

        if (_registeredText == _settings.GlobalHotkeyText)
        {
            return;
        }

        UnregisterCurrent();
        _registeredText = _settings.GlobalHotkeyText;

        if (!HotkeyGesture.TryParse(_settings.GlobalHotkeyText, out var gesture))
        {
            Log.Warning("全局快捷键无效: {Hotkey}", _settings.GlobalHotkeyText);
            return;
        }

        _registered = NativeMethods.RegisterHotKey(
            _messageHwnd,
            HotkeyId,
            gesture.Modifiers,
            (uint)gesture.Key);

        if (!_registered)
        {
            Log.Warning(
                "注册全局快捷键失败 hotkey={Hotkey} error={Error}",
                _settings.GlobalHotkeyText,
                Marshal.GetLastPInvokeError());
        }
    }

    private void UnregisterCurrent()
    {
        if (!_registered || _messageHwnd == nint.Zero)
        {
            _registered = false;
            return;
        }

        NativeMethods.UnregisterHotKey(_messageHwnd, HotkeyId);
        _registered = false;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            if (App.IsRecordingGlobalHotkey)
            {
                return nint.Zero;
            }

            _action();
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void SuspendRegistration()
    {
        _suspended = true;
        UnregisterCurrent();
        _registeredText = null;
    }

    public void ResumeRegistration()
    {
        _suspended = false;
        RegisterFromSettings();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        UnregisterCurrent();

        if (_messageHwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_messageHwnd);
            _messageHwnd = nint.Zero;
        }
    }
}
