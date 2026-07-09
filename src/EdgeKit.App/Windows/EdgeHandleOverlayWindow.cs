using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using EdgeKit.Core.Services;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;

namespace EdgeKit.App.Windows;

/// <summary>
/// 屏幕边缘可点击呼出长条。分层窗口提供逐像素透明，且不激活当前前台应用。
/// </summary>
public sealed class EdgeHandleOverlayWindow : IDisposable
{
    private const int AnimationDurationMs = 150;
    private const double HiddenProgress = 0.001;

    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly DispatcherQueueTimer _animationTimer;
    private nint _hwnd;
    private bool _visible;
    private bool _disposed;
    private EdgeTriggerSide _side = EdgeTriggerSide.Left;
    private NativeMethods.RECT _bounds;
    private NativeMethods.RECT _work;
    private long _animationStartTicks;
    private double _animationFrom;
    private double _animationTo;
    private double _animationProgress = HiddenProgress;
    private bool _hideWhenAnimationCompletes;

    public EdgeHandleOverlayWindow(DispatcherQueue dispatcherQueue)
    {
        _wndProc = WndProc;
        _className = "EdgeKitHandleOverlay_" + Guid.NewGuid().ToString("N");
        CreateWindow();

        _animationTimer = dispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(15);
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += OnAnimationTick;
    }

    public event EventHandler<EdgeHandleClickedEventArgs>? Clicked;

    public bool IsVisible => _visible;

    public EdgeTriggerSide CurrentSide => _side;

    public bool ContainsPoint(int x, int y)
        => _visible
            && x >= _bounds.Left
            && x < _bounds.Right
            && y >= _bounds.Top
            && y < _bounds.Bottom;

    public void Show(EdgeTriggerSide side, NativeMethods.RECT work, int width, int height)
    {
        if (_disposed)
        {
            return;
        }

        _side = side;
        _work = work;

        width = Math.Clamp(width, 24, 80);
        height = Math.Clamp(height, 120, Math.Max(120, work.Height));
        var y = work.Top + Math.Max(0, (work.Height - height) / 2);
        var x = side == EdgeTriggerSide.Left
            ? work.Left
            : work.Right - width;

        _bounds = new NativeMethods.RECT
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height
        };

        if (!_visible)
        {
            _animationProgress = HiddenProgress;
            ApplyAnimatedFrame();
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);
            _visible = true;
        }
        else
        {
            ApplyAnimatedFrame();
        }

        StartAnimation(1.0, hideWhenComplete: false);
    }

    public void Hide()
    {
        if (!_visible || _disposed)
        {
            return;
        }

        StartAnimation(HiddenProgress, hideWhenComplete: true);
    }

    private void CreateWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            hCursor = NativeMethods.LoadCursor(nint.Zero, NativeMethods.IDC_HAND),
            lpszClassName = _className
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("注册边缘呼出长条窗口类失败。");
        }

        var exStyle = (uint)(NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_NOACTIVATE);

        _hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            _className,
            "EdgeKit Handle Overlay",
            NativeMethods.WS_POPUP,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            instance,
            nint.Zero);

        if (_hwnd == nint.Zero)
        {
            throw new InvalidOperationException("创建边缘呼出长条窗口失败。");
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg is NativeMethods.WM_SETCURSOR or NativeMethods.WM_MOUSEMOVE)
        {
            NativeMethods.SetCursor(NativeMethods.LoadCursor(nint.Zero, NativeMethods.IDC_HAND));
            return msg == NativeMethods.WM_SETCURSOR ? new nint(1) : NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        if (msg == NativeMethods.WM_LBUTTONUP)
        {
            var side = _side;
            var work = _work;
            HideImmediately();
            Clicked?.Invoke(this, new EdgeHandleClickedEventArgs(side, work));
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void StartAnimation(double to, bool hideWhenComplete)
    {
        _animationFrom = _animationProgress;
        _animationTo = to;
        _hideWhenAnimationCompletes = hideWhenComplete;
        _animationStartTicks = Environment.TickCount64;
        _animationTimer.Start();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = Environment.TickCount64 - _animationStartTicks;
        var t = Math.Clamp(elapsed / (double)AnimationDurationMs, 0.0, 1.0);
        var eased = _animationTo > _animationFrom
            ? EaseOutBack(t)
            : 1 - Math.Pow(1 - t, 3);

        _animationProgress = Math.Clamp(_animationFrom + ((_animationTo - _animationFrom) * eased), HiddenProgress, 1.0);
        ApplyAnimatedFrame();

        if (t < 1.0)
        {
            return;
        }

        _animationTimer.Stop();
        _animationProgress = _animationTo;
        ApplyAnimatedFrame();

        if (_hideWhenAnimationCompletes)
        {
            HideImmediately();
        }
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + (c3 * Math.Pow(t - 1, 3)) + (c1 * Math.Pow(t - 1, 2));
    }

    private void ApplyAnimatedFrame()
    {
        var fullWidth = Math.Max(1, _bounds.Width);
        var visibleWidth = Math.Clamp((int)Math.Round(fullWidth * _animationProgress), 1, fullWidth);
        var x = _side == EdgeTriggerSide.Left
            ? _bounds.Left
            : _bounds.Right - visibleWidth;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            _bounds.Top,
            visibleWidth,
            _bounds.Height,
            NativeMethods.SWP_NOACTIVATE);

        Render(visibleWidth, _bounds.Height);
    }

    private void HideImmediately()
    {
        _animationTimer.Stop();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _visible = false;
        _animationProgress = HiddenProgress;
        _hideWhenAnimationCompletes = false;
    }

    private void Render(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = CreateHandlePath(width, height);
            using var fill = new SolidBrush(Color.FromArgb(31, 255, 255, 255));
            using var stroke = new Pen(Color.FromArgb(34, 255, 255, 255), 1f);

            g.FillPath(fill, path);
            g.DrawPath(stroke, path);

            var gripX = width / 2f;
            var gripHeight = Math.Min(96f, Math.Max(44f, height * 0.18f));
            var gripTop = (height - gripHeight) / 2f;
            var gripWidth = Math.Clamp(width * 0.22f, 3f, 5f);
            using var gripGlow = new Pen(Color.FromArgb(86, 94, 161, 255), Math.Max(7f, gripWidth + 5f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var grip = new Pen(Color.FromArgb(255, 94, 161, 255), gripWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            g.DrawLine(gripGlow, gripX, gripTop, gripX, gripTop + gripHeight);
            g.DrawLine(grip, gripX, gripTop, gripX, gripTop + gripHeight);
        }

        PushToLayeredWindow(bitmap, width, height);
    }

    private GraphicsPath CreateHandlePath(int width, int height)
    {
        const float inset = 0.75f;
        var radius = Math.Min(12f, Math.Max(6f, (width - (inset * 2)) / 2f));
        var diameter = radius * 2f;
        var left = inset;
        var top = inset;
        var right = width - inset;
        var bottom = height - inset;
        var path = new GraphicsPath();

        if (_side == EdgeTriggerSide.Left)
        {
            path.StartFigure();
            path.AddLine(0, top, right - radius, top);
            path.AddArc(right - diameter, top, diameter, diameter, 270, 90);
            path.AddLine(right, top + radius, right, bottom - radius);
            path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
            path.AddLine(right - radius, bottom, 0, bottom);
            path.CloseFigure();
        }
        else
        {
            path.StartFigure();
            path.AddLine(width, top, left + radius, top);
            path.AddArc(left, top, diameter, diameter, 270, -90);
            path.AddLine(left, top + radius, left, bottom - radius);
            path.AddArc(left, bottom - diameter, diameter, diameter, 180, -90);
            path.AddLine(left + radius, bottom, width, bottom);
            path.CloseFigure();
        }

        return path;
    }

    private void PushToLayeredWindow(Bitmap bitmap, int width, int height)
    {
        var screenDc = NativeMethods.GetDC(nint.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);
        var x = _side == EdgeTriggerSide.Left
            ? _bounds.Left
            : _bounds.Right - width;

        try
        {
            var size = new NativeMethods.SIZE { cx = width, cy = height };
            var srcPoint = new NativeMethods.POINT { X = 0, Y = 0 };
            var dstPoint = new NativeMethods.POINT { X = x, Y = _bounds.Top };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            NativeMethods.UpdateLayeredWindow(
                _hwnd,
                screenDc,
                ref dstPoint,
                ref size,
                memDc,
                ref srcPoint,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animationTimer.Tick -= OnAnimationTick;
        _animationTimer.Stop();
        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }
}

public sealed class EdgeHandleClickedEventArgs : EventArgs
{
    public EdgeHandleClickedEventArgs(EdgeTriggerSide side, NativeMethods.RECT work)
    {
        Side = side;
        Work = work;
    }

    public EdgeTriggerSide Side { get; }

    public NativeMethods.RECT Work { get; }
}
