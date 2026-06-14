using System;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 停靠几何与有效宽度计算。</summary>
public sealed partial class DrawerWindow
{
    /// <summary>
    /// 计算抽屉停靠几何：贴当前触发显示器的指定边缘，高度占满工作区，宽度取设置值（含菜单展开增量）。
    /// 同时算出完全隐藏时的屏幕外 X（停靠位置左移一个宽度）。
    /// </summary>
    private void ComputeDockGeometry()
    {
        var work = GetDockWorkArea();

        _dockWidth = ClampDrawerWidth(_settings.DrawerWidth, ToolNav.IsPaneOpen);
        _dockHeight = work.Height;
        _dockY = work.Top;

        var effective = EffectiveWidth();
        if (_currentSide == EdgeTriggerSide.Right)
        {
            _dockedX = work.Right - effective;
            _hiddenX = work.Right;
        }
        else
        {
            _dockedX = work.Left;
            _hiddenX = work.Left - effective;
        }

        AppWindowRef.Resize(new SizeInt32(effective, _dockHeight));
        UpdateResizeThumbPlacement();
    }

    private NativeMethods.RECT GetDockWorkArea()
    {
        if (_pendingWorkArea is { } pending)
        {
            _pendingWorkArea = null;
            return pending;
        }

        return ScreenInterop.GetWorkAreaAtCursor();
    }

    /// <summary>当前 DPI 缩放比例。优先用 XamlRoot，回退到 GetDpiForWindow。</summary>
    private double GetDpiScale()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 0;
        if (scale > 0)
        {
            return scale;
        }

        var dpi = _hwnd != 0 ? NativeMethods.GetDpiForWindow(_hwnd) : 96u;
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// 抽屉实际窗口宽度（物理像素）：基础宽度 + 菜单展开时多出的 pane 宽度。
    /// 这样菜单展开时窗口整体加宽，内容区不被压缩。
    /// </summary>
    private int EffectiveWidth() => EffectiveWidth(ToolNav.IsPaneOpen);

    private int EffectiveWidth(bool expanded)
    {
        return _dockWidth + GetPaneExtraWidth(expanded);
    }

    private int GetPaneExtraWidth(bool expanded)
        => expanded
            ? (int)Math.Round((OpenPaneLengthDip - CompactPaneLengthDip) * GetDpiScale())
            : 0;

    private int MaxEffectiveWidth()
        => Math.Max(MinDrawerWidth, (int)Math.Floor(GetDockWorkAreaWithoutConsumingPending().Width * MaxDrawerScreenRatio));

    private NativeMethods.RECT GetDockWorkAreaWithoutConsumingPending()
        => _pendingWorkArea ?? ScreenInterop.GetWorkAreaAtCursor();

    private int MaxDrawerWidth(bool expanded)
        => Math.Max(MinDrawerWidth, MaxEffectiveWidth() - GetPaneExtraWidth(expanded));

    private int ClampDrawerWidth(int width, bool expanded)
        => Math.Clamp(width, MinDrawerWidth, MaxDrawerWidth(expanded));

    private void ClampCurrentDrawerWidth(bool expanded, bool save)
    {
        var clamped = ClampDrawerWidth(_dockWidth, expanded);
        if (clamped == _dockWidth)
        {
            return;
        }

        _dockWidth = clamped;
        if (save && _settings.DrawerWidth != clamped)
        {
            _settings.DrawerWidth = clamped;
            _settings.Save();
        }
    }

    /// <summary>按当前展开状态重算并应用窗口宽度与隐藏位。</summary>
    private void ApplyEffectiveWidth() => ApplyWidth(ToolNav.IsPaneOpen);

    /// <summary>按指定展开状态重算并应用窗口宽度与隐藏位。</summary>
    private void ApplyWidth(bool expanded)
    {
        ClampCurrentDrawerWidth(expanded, save: true);

        var effective = EffectiveWidth(expanded);
        _hiddenX = _currentSide == EdgeTriggerSide.Right
            ? _dockedX + effective
            : _dockedX - effective;

        if (_currentSide == EdgeTriggerSide.Right && _state != DrawerState.Hidden)
        {
            var right = AppWindowRef.Position.X + AppWindowRef.Size.Width;
            _dockedX = right - effective;
            AppWindowRef.Move(new PointInt32(_dockedX, _dockY));
            _hiddenX = right;
        }

        AppWindowRef.Resize(new SizeInt32(effective, _dockHeight));

        if (_state == DrawerState.Hidden)
        {
            AppWindowRef.Move(new PointInt32(_hiddenX, _dockY));
        }
    }
}
