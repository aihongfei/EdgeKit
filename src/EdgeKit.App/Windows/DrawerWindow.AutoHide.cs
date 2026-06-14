using System;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 边缘触发与自动收起。</summary>
public sealed partial class DrawerWindow
{
    private void OnEdgeHandleClicked(object? sender, EdgeHandleClickedEventArgs e)
    {
        _currentSide = e.Side;
        _pendingWorkArea = e.Work;
        ShowDrawer();
    }

    private void OnAutoHideTick(DispatcherQueueTimer sender, object args)
    {
        if (_state != DrawerState.Shown || _isPinned || _isResizing || ShouldKeepOpenForSearchInput())
        {
            _cursorOutsideSinceTicks = null;
            return;
        }

        var (cx, cy) = ScreenInterop.GetCursorPosition();

        // 抽屉当前屏幕矩形（含菜单展开增量）。
        var left = _dockedX;
        var top = _dockY;
        var right = _dockedX + EffectiveWidth();
        var bottom = _dockY + _dockHeight;

        var inside = cx >= left && cx < right && cy >= top && cy < bottom;

        if (inside)
        {
            _cursorOutsideSinceTicks = null;
            return;
        }

        var now = Environment.TickCount64;
        if (_cursorOutsideSinceTicks is null)
        {
            _cursorOutsideSinceTicks = now;
            return;
        }

        if (now - _cursorOutsideSinceTicks.Value >= _settings.AutoHideDelayMs)
        {
            HideDrawer();
        }
    }

    private bool IsSearchInputActiveInDrawer()
        => IsDrawerForegroundWindow()
            && SearchInput.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;

    private bool ShouldKeepOpenForSearchInput()
    {
        if (!_keepOpenForSearchInput)
        {
            return IsSearchInputActiveInDrawer();
        }

        if (IsDrawerForegroundWindow())
        {
            _searchKeepOpenSawDrawerForeground = true;
            return true;
        }

        if (!_searchKeepOpenSawDrawerForeground)
        {
            var activationGraceMs = Math.Max(1200, _settings.AnimationDurationMs + 700);
            if (Environment.TickCount64 - _searchKeepOpenStartedTicks <= activationGraceMs)
            {
                EnsureDrawerForeground();
                return true;
            }
        }

        StopSearchInputKeepOpen();
        return false;
    }

    private bool IsDrawerForegroundWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        if (foreground == _hwnd || NativeMethods.IsChild(_hwnd, foreground))
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }
}
