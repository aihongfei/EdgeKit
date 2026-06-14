using System.Runtime.InteropServices;
using EdgeKit.Native;
using Windows.System;

namespace EdgeKit.App.Interaction;

public sealed class HotkeyRecorder : IDisposable
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Action<HotkeyGesture> _recorded;
    private readonly Action<string> _previewChanged;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private nint _hook;
    private bool _controlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _winDown;
    private bool _disposed;

    public HotkeyRecorder(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        Action<HotkeyGesture> recorded,
        Action<string> previewChanged)
    {
        _dispatcher = dispatcher;
        _recorded = recorded;
        _previewChanged = previewChanged;
        _hookProc = HookProc;
    }

    public bool Start()
    {
        if (_hook != nint.Zero)
        {
            return true;
        }

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);

        return _hook != nint.Zero;
    }

    public void Stop()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = nint.Zero;
        _controlDown = false;
        _altDown = false;
        _shiftDown = false;
        _winDown = false;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = unchecked((uint)wParam.ToInt64());
        if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN
            or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var key = (VirtualKey)info.vkCode;

            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                _dispatcher.TryEnqueue(() => HandleKeyDown(key));
            }
            else
            {
                _dispatcher.TryEnqueue(() => HandleKeyUp(key));
            }

            return 1;
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleKeyDown(VirtualKey key)
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        if (SetModifierState(key, isDown: true))
        {
            _previewChanged(GetModifierPreview());
            return;
        }

        _recorded(new HotkeyGesture(
            _controlDown,
            _altDown,
            _shiftDown,
            _winDown,
            key));
    }

    private void HandleKeyUp(VirtualKey key)
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        if (!SetModifierState(key, isDown: false))
        {
            return;
        }

        if (!_controlDown && !_altDown && !_shiftDown && !_winDown
            && HotkeyGesture.TryCreateModifierOnly(key, out var gesture))
        {
            _recorded(gesture);
            return;
        }

        _previewChanged(GetModifierPreview());
    }

    private bool SetModifierState(VirtualKey key, bool isDown)
    {
        switch (key)
        {
            case VirtualKey.Control:
            case VirtualKey.LeftControl:
            case VirtualKey.RightControl:
                _controlDown = isDown;
                return true;
            case VirtualKey.Menu:
            case VirtualKey.LeftMenu:
            case VirtualKey.RightMenu:
                _altDown = isDown;
                return true;
            case VirtualKey.Shift:
            case VirtualKey.LeftShift:
            case VirtualKey.RightShift:
                _shiftDown = isDown;
                return true;
            case VirtualKey.LeftWindows:
            case VirtualKey.RightWindows:
                _winDown = isDown;
                return true;
            default:
                return false;
        }
    }

    private string GetModifierPreview()
    {
        var parts = new List<string>();
        if (_controlDown)
        {
            parts.Add("Ctrl");
        }

        if (_altDown)
        {
            parts.Add("Alt");
        }

        if (_shiftDown)
        {
            parts.Add("Shift");
        }

        if (_winDown)
        {
            parts.Add("Win");
        }

        return parts.Count == 0
            ? "按下快捷键..."
            : string.Join("+", parts) + "+...";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
