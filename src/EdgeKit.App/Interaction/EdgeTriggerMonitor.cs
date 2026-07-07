using System;
using EdgeKit.App.Windows;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 监视屏幕边缘并显示可点击呼出长条。长条单击后再打开抽屉，避免触边直接打断用户。
/// </summary>
public sealed class EdgeTriggerMonitor : IDisposable
{
    private const int PollMs = 30;
    private const int EdgeContactTolerance = 2;
    private const int HandleHideDelayMs = 250;

    private readonly ISettingsService _settings;
    private readonly EdgeHandleOverlayWindow _handleOverlay;
    private readonly DispatcherQueueTimer _timer;

    private Func<bool>? _isDrawerVisible;
    private long? _outsideSinceTicks;
    private bool _disposed;

    public EdgeTriggerMonitor(ISettingsService settings, DispatcherQueue dispatcherQueue)
    {
        _settings = settings;
        _handleOverlay = new EdgeHandleOverlayWindow(dispatcherQueue);
        _handleOverlay.Clicked += OnHandleClicked;

        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PollMs);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    public event EventHandler<EdgeHandleClickedEventArgs>? HandleClicked;

    public void Start() => _timer.Start();

    public void SetDrawerVisibleProbe(Func<bool> probe) => _isDrawerVisible = probe;

    public void Stop()
    {
        _timer.Stop();
        HideHandleAndReset();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!_settings.EdgeTriggerEnabled)
        {
            HideHandleAndReset();
            return;
        }

        if (_isDrawerVisible?.Invoke() == true)
        {
            HideHandleAndReset();
            return;
        }

        var (cursorX, cursorY) = ScreenInterop.GetCursorPosition();
        var work = ScreenInterop.GetWorkAreaAtCursor();

        if (!_handleOverlay.IsVisible)
        {
            TryShowHandle(cursorX, cursorY, work);
            return;
        }

        UpdateVisibleHandleLifetime(cursorX, cursorY, work);
    }

    private void TryShowHandle(int cursorX, int cursorY, NativeMethods.RECT work)
    {
        if (!_settings.EdgeTriggerEnabled)
        {
            return;
        }

        if (_settings.DisableInFullscreen && ScreenInterop.IsForegroundWindowFullscreen())
        {
            return;
        }

        if (cursorY < work.Top || cursorY >= work.Bottom)
        {
            return;
        }

        if (IsSideEnabled(EdgeTriggerSide.Left)
            && cursorX <= work.Left + EdgeContactTolerance)
        {
            ShowHandle(EdgeTriggerSide.Left, work);
            return;
        }

        if (IsSideEnabled(EdgeTriggerSide.Right)
            && cursorX >= work.Right - EdgeContactTolerance)
        {
            ShowHandle(EdgeTriggerSide.Right, work);
        }
    }

    private void ShowHandle(EdgeTriggerSide side, NativeMethods.RECT work)
    {
        _outsideSinceTicks = null;
        _handleOverlay.Show(side, work, _settings.EdgeHandleWidth, _settings.EdgeHandleHeight);
    }

    private void UpdateVisibleHandleLifetime(int cursorX, int cursorY, NativeMethods.RECT work)
    {
        if (TrySwitchVisibleHandleSide(cursorX, cursorY, work))
        {
            return;
        }

        var onEnabledEdge = cursorY >= work.Top
            && cursorY < work.Bottom
            && ((IsSideEnabled(EdgeTriggerSide.Left) && cursorX <= work.Left + EdgeContactTolerance)
                || (IsSideEnabled(EdgeTriggerSide.Right) && cursorX >= work.Right - EdgeContactTolerance));

        if (_handleOverlay.ContainsPoint(cursorX, cursorY) || onEnabledEdge)
        {
            _outsideSinceTicks = null;
            return;
        }

        var now = Environment.TickCount64;
        if (_outsideSinceTicks is null)
        {
            _outsideSinceTicks = now;
            return;
        }

        if (now - _outsideSinceTicks.Value >= HandleHideDelayMs)
        {
            _handleOverlay.Hide();
            _outsideSinceTicks = null;
        }
    }

    private bool TrySwitchVisibleHandleSide(int cursorX, int cursorY, NativeMethods.RECT work)
    {
        if (!_settings.EdgeTriggerEnabled)
        {
            return false;
        }

        if (cursorY < work.Top || cursorY >= work.Bottom)
        {
            return false;
        }

        if (_handleOverlay.CurrentSide != EdgeTriggerSide.Left
            && IsSideEnabled(EdgeTriggerSide.Left)
            && cursorX <= work.Left + EdgeContactTolerance)
        {
            ShowHandle(EdgeTriggerSide.Left, work);
            return true;
        }

        if (_handleOverlay.CurrentSide != EdgeTriggerSide.Right
            && IsSideEnabled(EdgeTriggerSide.Right)
            && cursorX >= work.Right - EdgeContactTolerance)
        {
            ShowHandle(EdgeTriggerSide.Right, work);
            return true;
        }

        return false;
    }

    private bool IsSideEnabled(EdgeTriggerSide side)
        => side == EdgeTriggerSide.Left
            ? (_settings.TriggerSides & EdgeTriggerSides.Left) != 0
            : (_settings.TriggerSides & EdgeTriggerSides.Right) != 0;

    private void OnHandleClicked(object? sender, EdgeHandleClickedEventArgs e)
    {
        if (!_settings.EdgeTriggerEnabled)
        {
            HideHandleAndReset();
            return;
        }

        _outsideSinceTicks = null;
        HandleClicked?.Invoke(this, e);
    }

    private void HideHandleAndReset()
    {
        _handleOverlay.Hide();
        _outsideSinceTicks = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= OnTick;
        _timer.Stop();
        _handleOverlay.Clicked -= OnHandleClicked;
        _handleOverlay.Dispose();
    }
}
