using System;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 滑入滑出与宽度过渡动画。</summary>
public sealed partial class DrawerWindow
{
    /// <summary>滑出抽屉（若已展开或正在展开则忽略）。</summary>
    public void ShowDrawer()
    {
        if (_state is DrawerState.Shown or DrawerState.SlidingIn)
        {
            return;
        }

        if (_pendingWorkArea is null)
        {
            _currentSide = GetDefaultTriggerSide();
        }

        // 每次重新计算几何，兼容工作区/分辨率变化。
        ComputeDockGeometry();

        // 确保窗口可见后从当前位置滑到停靠位。
        AppWindowRef.Show();
        // 窗口显示后重新应用一次去边框，构造期设置常不落地。
        ScreenInterop.RemoveWindowBorder(_hwnd);
        AppWindowRef.Move(new PointInt32(GetCurrentX(), _dockY));
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);

        StartAnimation(_dockedX, DrawerState.SlidingIn);
    }

    /// <summary>收起抽屉（若已隐藏或正在收起则忽略）。固定状态下不收起。</summary>
    public void HideDrawer()
    {
        if (_isPinned)
        {
            return;
        }

        if (_state is DrawerState.Hidden or DrawerState.SlidingOut)
        {
            return;
        }

        StartAnimation(_hiddenX, DrawerState.SlidingOut);
    }

    private int GetCurrentX()
    {
        var pos = AppWindowRef.Position;
        return pos.X;
    }

    private EdgeTriggerSide GetDefaultTriggerSide()
        => (_settings.TriggerSides & EdgeTriggerSides.Left) != 0
            ? EdgeTriggerSide.Left
            : EdgeTriggerSide.Right;

    private void StartAnimation(int toX, DrawerState state)
    {
        _animFromX = GetCurrentX();
        _animToX = toX;
        _animStartTicks = Environment.TickCount64;
        _state = state;
        _animationTimer.Start();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var duration = Math.Max(1, _settings.AnimationDurationMs);
        var elapsed = Environment.TickCount64 - _animStartTicks;
        var t = Math.Clamp(elapsed / (double)duration, 0.0, 1.0);

        // ease-out cubic，结尾自然减速。
        var eased = 1 - Math.Pow(1 - t, 3);
        var x = (int)Math.Round(_animFromX + ((_animToX - _animFromX) * eased));

        AppWindowRef.Move(new PointInt32(x, _dockY));

        if (t >= 1.0)
        {
            _animationTimer.Stop();
            AppWindowRef.Move(new PointInt32(_animToX, _dockY));

            if (_state == DrawerState.SlidingIn)
            {
                _state = DrawerState.Shown;
                _cursorOutsideSinceTicks = null;
                _autoHideTimer.Start();
            }
            else if (_state == DrawerState.SlidingOut)
            {
                _state = DrawerState.Hidden;
                _autoHideTimer.Stop();
                ResetSearchFocusState();
                AppWindowRef.Hide();
            }
        }
    }

    /// <summary>
    /// 平滑地把窗口宽度过渡到目标宽度（物理像素），与 NavigationView pane 展开动画观感同步。
    /// 仅在抽屉已展开（Shown）时使用，X 固定不动。
    /// </summary>
    private void AnimateWidth(int targetWidth)
    {
        _widthAnimFrom = AppWindowRef.Size.Width;
        _widthAnimTo = targetWidth;
        _widthAnimRightEdge = AppWindowRef.Position.X + _widthAnimFrom;
        _widthAnimStartTicks = Environment.TickCount64;
        _widthAnimTimer.Start();
    }

    private void OnWidthAnimTick(DispatcherQueueTimer sender, object args)
    {
        var duration = Math.Max(1, _settings.AnimationDurationMs);
        var elapsed = Environment.TickCount64 - _widthAnimStartTicks;
        var t = Math.Clamp(elapsed / (double)duration, 0.0, 1.0);

        // 与滑入滑出一致的 ease-out cubic。
        var eased = 1 - Math.Pow(1 - t, 3);
        var w = (int)Math.Round(_widthAnimFrom + ((_widthAnimTo - _widthAnimFrom) * eased));

        if (_currentSide == EdgeTriggerSide.Right)
        {
            _dockedX = _widthAnimRightEdge - w;
            AppWindowRef.Move(new PointInt32(_dockedX, _dockY));
        }

        AppWindowRef.Resize(new SizeInt32(w, _dockHeight));

        if (t >= 1.0)
        {
            _widthAnimTimer.Stop();
            if (_currentSide == EdgeTriggerSide.Right)
            {
                _dockedX = _widthAnimRightEdge - _widthAnimTo;
                AppWindowRef.Move(new PointInt32(_dockedX, _dockY));
            }

            AppWindowRef.Resize(new SizeInt32(_widthAnimTo, _dockHeight));
            _hiddenX = _currentSide == EdgeTriggerSide.Right
                ? _dockedX + _widthAnimTo
                : _dockedX - _widthAnimTo;
        }
    }
}
