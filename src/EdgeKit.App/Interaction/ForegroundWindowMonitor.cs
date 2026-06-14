using System;
using System.Collections.Concurrent;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 前台窗口监视器：用 <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c> 被动接收前台切换，
/// 把符合条件的应用窗口记录为"最近活动窗口"。
///
/// 设计：
/// - 钩子回调在安装钩子的线程（UI 线程）触发，可直接访问设置与仓储。
/// - 过滤掉工具窗口、cloaked、无标题、EdgeKit 自身进程。
/// - 维护"持久化 Key → 实时句柄"的内存映射，供首页激活时优先复用活动窗口。
/// - 受 <see cref="ISettingsService.TrackRecentWindows"/> 开关控制。
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IRecentItemsRepository _repository;

    // 持久化 Key → 当前会话内的实时窗口句柄。
    private readonly ConcurrentDictionary<string, nint> _liveHandles = new(StringComparer.OrdinalIgnoreCase);

    // 必须持有委托引用，防止被 GC 回收导致回调失效。
    private NativeMethods.WinEventDelegate? _callback;
    private nint _hook;
    private uint _ownProcessId;

    public ForegroundWindowMonitor(ISettingsService settings, IRecentItemsRepository repository)
    {
        _settings = settings;
        _repository = repository;
    }

    /// <summary>
    /// 在 UI 线程安装前台事件钩子。<paramref name="dispatcherQueue"/> 仅用于标识线程亲和性，
    /// 实际回调由系统投递到本线程消息循环。
    /// </summary>
    public void Start(DispatcherQueue dispatcherQueue)
    {
        _ = dispatcherQueue;
        _ownProcessId = (uint)Environment.ProcessId;

        _callback = OnForegroundChanged;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_hook == nint.Zero)
        {
            Log.Warning("SetWinEventHook 安装前台窗口钩子失败");
        }
    }

    /// <summary>
    /// 解析持久化 Key 对应的当前实时句柄（仅本次会话内有效）。未找到返回 <see cref="nint.Zero"/>。
    /// </summary>
    public nint ResolveLiveHandle(string key)
        => _liveHandles.TryGetValue(key, out var hwnd) ? hwnd : nint.Zero;

    /// <summary>
    /// 从持久化 Key 中解析出进程可执行文件路径（Key 形如 "路径|标题"）。
    /// </summary>
    public static string ExtractProcessPath(string key)
    {
        var idx = key.IndexOf('|');
        return idx > 0 ? key[..idx] : string.Empty;
    }

    private void OnForegroundChanged(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (!_settings.TrackRecentWindows || hwnd == nint.Zero)
            {
                return;
            }

            // 排除 EdgeKit 自身窗口。
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownProcessId)
            {
                return;
            }

            if (!WindowQuery.IsEligibleWindow(hwnd))
            {
                return;
            }

            var title = WindowQuery.GetTitle(hwnd);
            var (processName, processPath) = WindowQuery.GetProcess(hwnd);
            if (string.IsNullOrEmpty(processName))
            {
                return;
            }

            // Key = 进程路径|标题（路径不含 '|'，可安全分割）。无路径时退化用进程名。
            var pathPart = string.IsNullOrEmpty(processPath) ? processName : processPath;
            var key = $"{pathPart}|{title}";

            _liveHandles[key] = hwnd;

            _repository.Touch(new RecentItem(
                RecentItemKind.Window,
                key,
                title,
                processName,
                "\uE737", // 占位窗口图标字形
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "记录前台窗口失败");
        }
    }

    public void Dispose()
    {
        if (_hook != nint.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }

        _callback = null;
    }
}