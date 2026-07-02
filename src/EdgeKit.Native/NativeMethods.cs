using System.Runtime.InteropServices;
using System.Text;

namespace EdgeKit.Native;

/// <summary>
/// EdgeKit 需要的 Win32 P/Invoke 封装。
/// 仅暴露抽屉交互所需的最小集合：光标位置、前台窗口、窗口矩形、显示器工作区、全屏判断。
/// </summary>
public static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // DwmSetWindowAttribute 属性枚举（dwmapi.h）。
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // 特殊颜色值：DWMWA_COLOR_NONE 表示去掉边框颜色（不绘制边框）。
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // DWM_WINDOW_CORNER_PREFERENCE。
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // ---- 窗口子类化（用于拦截 WM_NCCALCSIZE 去掉非客户区边框） ----

    public const int GWLP_WNDPROC = -4;
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint WM_NCHITTEST = 0x0084;

    // WM_NCHITTEST 返回值：边框/四角拖拽调整大小。
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    public const uint WM_USER = 0x0400;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_NULL = 0x0000;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    public delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, WndProcDelegate newProc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

    [DllImport("user32.dll")]
    public static extern nint CallWindowProc(nint prevProc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        nint hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DnsFlushResolverCache();

    // ---- 系统托盘与原生弹出菜单 ----

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImage(nint hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint TrackPopupMenu(
        nint hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        nint hWnd,
        nint prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    // ---- 剪贴板格式监听 ----

    // 剪贴板内容变化时，系统向已注册窗口投递此消息。
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    public const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLayeredWindowAttributes(nint hwnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        string lParam,
        uint fuFlags,
        uint timeout,
        out nint result);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public static readonly nint HWND_TOPMOST = new(-1);
    public static readonly nint HWND_NOTOPMOST = new(-2);
    public static readonly nint HWND_BROADCAST = new(0xffff);

    // ---- 前台窗口事件钩子（用于记录最近活动窗口） ----

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    // ---- 窗口标题 / 可见性 / 进程 / 样式 ----

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsChild(nint hWndParent, nint hWnd);

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(nint hWnd, int nIndex);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // DWM cloaked 判断（用于排除 UWP 隐藏外壳窗口）。
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ---- 激活窗口 ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    // ---- 窗口图标 ----

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetClassLongPtr(nint hWnd, int nIndex);

    public const uint WM_GETICON = 0x007F;
    public const nint ICON_SMALL = 0;
    public const nint ICON_BIG = 1;
    public const nint ICON_SMALL2 = 2;
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    // ---- Shell AppsFolder 应用枚举（Win32 + UWP/商店应用） ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint hObject);

    // BHID_EnumItems：把 IShellItem 当作容器枚举其子项。
    public static readonly Guid BHID_EnumItems =
        new("94f60519-2850-4924-aa5a-d15e84868039");

    // SIGDN 显示名标志。
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    // 相对父容器的解析名：对 shell:AppsFolder 的子项即为可启动的 AppUserModelID
    // （UWP/商店应用与已注册的 Win32 程序均适用）。
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    // SIIGBF：IShellItemImageFactory.GetImage 取图标的标志。
    public const uint SIIGBF_RESIZETOFIT = 0x00000000;
    public const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    public const uint SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(
            nint pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        void Reset();

        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out nint phbm);
    }

    public static readonly Guid IID_IShellItem =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public static readonly Guid IID_IShellItem2 =
        new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem2
    {
        void BindToHandler(
            nint pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);

        [PreserveSig]
        int GetPropertyStore(
            uint flags,
            [In] ref Guid riid,
            out nint ppv);
    }

    // ---- ShellLink 快捷方式读取（只读 .lnk 目标与 AUMID） ----

    public const int MAX_PATH = 260;
    public const uint SLGP_RAWPATH = 0x0004;
    public const uint STGM_READ = 0x00000000;
    public const uint GPS_DEFAULT = 0x00000000;
    public const uint GPS_READWRITE = 0x00000002;
    public const ushort VT_LPWSTR = 31;

    public static readonly Guid BHID_PropertyStore =
        new("0384e1a4-1523-439c-a4c8-ab911052f586");

    public static readonly PropertyKey PKEY_AppUserModel_ID = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    public static readonly Guid IID_IPropertyStore =
        new("00000138-0000-0000-C000-000000000046");

    public const int S_OK = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        uint flags,
        [In] ref Guid riid,
        out nint ppv);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    public sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            nint pfd,
            uint fFlags);

        void GetIDList(out nint ppidl);

        void SetIDList(nint pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(nint hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("00000138-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint cProps);

        void GetAt(uint iProp, out PropertyKey pkey);

        void GetValue(ref PropertyKey key, out PropVariant pv);

        void SetValue(ref PropertyKey key, ref PropVariant propvar);

        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct PropertyKey
    {
        public PropertyKey(Guid fmtid, uint pid)
        {
            FormatId = fmtid;
            PropertyId = pid;
        }

        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint p;

        public readonly string GetString()
            => vt == 31 && p != nint.Zero ? Marshal.PtrToStringUni(p) ?? string.Empty : string.Empty;

        public void Clear() => PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pvar);

    // ---- System memory ----

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- 光标 ----

    public const int IDC_ARROW = 32512;
    public const int IDC_HAND = 32649;
    public const int IDC_SIZEWE = 32644;

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Auto)]
    public static extern nint LoadCursor(nint hInstance, int lpCursorName);

    [DllImport("user32.dll", SetLastError = false)]
    public static extern nint SetCursor(nint hCursor);

    // ---- 扩展窗口样式设置（用于覆盖层 click-through / layered） ----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    // ---- Raw Input（读取被边缘钳制后仍存在的原始鼠标位移，用于"推墙"手势） ----

    public const int RIM_TYPEMOUSE = 0;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint WM_INPUT = 0x00FF;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    // RAWMOUSE.usFlags：相对/绝对移动标志。
    public const ushort MOUSE_MOVE_RELATIVE = 0x00;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    // RAWMOUSE 的精简布局：我们只关心 usFlags 与 lLastX/lLastY。
    // 通过显式偏移把按钮/数据联合区跳过，确保 lLastX/lLastY 落在正确偏移。
    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSE
    {
        [FieldOffset(0)]
        public ushort usFlags;

        [FieldOffset(4)]
        public uint ulButtons;

        [FieldOffset(8)]
        public uint ulRawButtons;

        [FieldOffset(12)]
        public int lLastX;

        [FieldOffset(16)]
        public int lLastY;

        [FieldOffset(20)]
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTMOUSE
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        nint hRawInput,
        uint uiCommand,
        nint pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    // ---- 分层窗口（UpdateLayeredWindow）：用于能量线覆盖层的真正逐像素透明 ----

    public const uint WS_POPUP = 0x80000000;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(
        nint hwnd,
        nint hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        nint hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    // ---- CPU 频率（powrprof.dll） ----

    public const int ProcessorInformation = 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern uint CallNtPowerInformation(
        int InformationLevel,
        nint InputBuffer,
        int InputBufferSize,
        [Out] PROCESSOR_POWER_INFORMATION[] OutputBuffer,
        int OutputBufferSize);
}
