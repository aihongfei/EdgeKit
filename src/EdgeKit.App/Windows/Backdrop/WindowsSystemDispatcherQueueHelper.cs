using System.Runtime.InteropServices;

namespace EdgeKit.App.Windows.Backdrop;

/// <summary>
/// 确保当前线程拥有一个 Windows.System.DispatcherQueue。
/// WinUI 3 的 SystemBackdrop（Mica / Acrylic）控制器在 unpackaged 场景下
/// 需要系统级 DispatcherQueue 才能渲染，否则 backdrop 不生效（表现为纯色背景）。
/// 这是 Windows App SDK 官方 sample 的标准做法。
/// </summary>
internal sealed class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    private object? _dispatcherQueueController;

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        // 已有系统 DispatcherQueue 则无需创建。
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() != null)
        {
            return;
        }

        if (_dispatcherQueueController != null)
        {
            return;
        }

        DispatcherQueueOptions options;
        options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
        options.threadType = 2;    // DQTYPE_THREAD_CURRENT
        options.apartmentType = 2; // DQTAT_COM_STA

        object? controller = null;
        CreateDispatcherQueueController(options, ref controller);
        _dispatcherQueueController = controller;
    }
}