using System;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 右边缘拖拽调宽、设置变更响应、固定/关闭。</summary>
public sealed partial class DrawerWindow
{
    private void OnResizeThumbPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var cursor = NativeMethods.LoadCursor(nint.Zero, NativeMethods.IDC_SIZEWE);
        NativeMethods.SetCursor(cursor);
    }

    private void OnResizeThumbPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var cursor = NativeMethods.LoadCursor(nint.Zero, NativeMethods.IDC_ARROW);
        NativeMethods.SetCursor(cursor);
    }

    private void OnResizeThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizing = true;
        _cursorOutsideSinceTicks = null;
        _widthAnimTimer.Stop();
    }

    private void OnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        _isResizing = true;
        _cursorOutsideSinceTicks = null;

        // DragDelta 提供的是 DIP，换算为物理像素后调整基础宽度，并限制实际总宽度不超过屏幕 80%。
        var deltaPx = (int)Math.Round(e.HorizontalChange * GetDpiScale());
        if (_currentSide == EdgeTriggerSide.Right)
        {
            deltaPx = -deltaPx;
        }

        var newWidth = ClampDrawerWidth(_dockWidth + deltaPx, ToolNav.IsPaneOpen);
        if (newWidth == _dockWidth)
        {
            return;
        }

        _dockWidth = newWidth;
        ApplyEffectiveWidth();
    }

    private void OnResizeThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizing = false;
        _cursorOutsideSinceTicks = null;
        _dockWidth = ClampDrawerWidth(_dockWidth, ToolNav.IsPaneOpen);
        ApplyEffectiveWidth();

        // 落库记忆最终基础宽度。
        _settings.DrawerWidth = _dockWidth;
        _settings.Save();
    }

    /// <summary>
    /// 设置变更回调：当基础宽度被设置页修改时，实时重算几何并调整窗口。
    /// 拖拽自身触发的变更因 _dockWidth 已同步而不会重复处理。
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var target = ClampDrawerWidth(_settings.DrawerWidth, ToolNav.IsPaneOpen);
        if (_settings.DrawerWidth != target)
        {
            _settings.DrawerWidth = target;
            _settings.Save();
        }

        if (target == _dockWidth)
        {
            // 宽度没变，但快捷键等设置可能变了，仍需刷新提示。
            UpdateHotkeyHint();
            _searchCoordinator.SetEverythingEnabled(_settings.UseEverythingSearch);
            UpdateSearchSourceVisuals();
            return;
        }

        _dockWidth = target;
        ApplyEffectiveWidth();
        UpdateHotkeyHint();
        _searchCoordinator.SetEverythingEnabled(_settings.UseEverythingSearch);
        UpdateSearchSourceVisuals();
    }

    private void UpdateResizeThumbPlacement()
    {
        if (ResizeThumb is null)
        {
            return;
        }

        ResizeThumb.HorizontalAlignment = _currentSide == EdgeTriggerSide.Right
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        SetPinned(!_isPinned);
    }

    private void SetPinned(bool pinned)
    {
        _isPinned = pinned;

        // 固定图标：取消固定时用空心图钉，固定时用实心图钉的视觉提示由 ToolTip 体现。
        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        PinButton.SetValue(ToolTipService.ToolTipProperty,
            pinned ? "取消固定" : "固定");

        if (!pinned && _state == DrawerState.Shown)
        {
            // 取消固定后重新评估自动收起。
            _cursorOutsideSinceTicks = null;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 关闭按钮：取消固定并立即收起。
        _isPinned = false;
        SetPinned(false);
        HideDrawer();
    }
}
