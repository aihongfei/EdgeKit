using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using EdgeKit.Native;

namespace EdgeKit.App.Windows;

/// <summary>
/// "推墙"蓄力能量线覆盖层。
///
/// 一个贴屏幕左缘、全屏高、置顶、不激活、点击穿透的分层窗口（WS_EX_LAYERED）。
/// 用 GDI+ 渲染到 32bpp ARGB 位图后经 UpdateLayeredWindow 上屏，得到真正的逐像素透明与柔和辉光，
/// 比 WinUI 透明窗口更可靠、开销更低。
///
/// 从锚点光标 Y 出发，按进度 p 向上下两个方向延展两条发光线；p 越大越亮、并带核心高光。
/// p 到 1 时整条边缘脉冲一下后由调用方隐藏。
/// </summary>
public sealed class EdgeChargeOverlayWindow : IDisposable
{
    // 覆盖层宽度（物理像素），贴在屏幕最左侧。
    private const int OverlayWidth = 48;

    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className;
    private nint _hwnd;
    private bool _visible;
    private bool _disposed;

    // 覆盖层屏幕位置与尺寸（物理像素）。
    private int _x;
    private int _y;
    private int _width = OverlayWidth;
    private int _height;

    public EdgeChargeOverlayWindow()
    {
        _wndProc = WndProc;
        _className = "EdgeKitChargeOverlay_" + Guid.NewGuid().ToString("N");
        CreateWindow();
    }

    private void CreateWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = _className
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("注册能量线覆盖层窗口类失败。");
        }

        var exStyle = (uint)(NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_NOACTIVATE);

        _hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            _className,
            "EdgeKit Charge Overlay",
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
            throw new InvalidOperationException("创建能量线覆盖层窗口失败。");
        }
    }

    /// <summary>
    /// 按蓄力进度更新能量线。anchorY 为锚点屏幕 Y，work 为所在显示器工作区。
    /// reset=true 表示取消蓄力，隐藏覆盖层。
    /// </summary>
    public void UpdateProgress(double progress, int anchorY, NativeMethods.RECT work, bool reset)
    {
        if (_disposed)
        {
            return;
        }

        if (reset || progress <= 0.0001)
        {
            Hide();
            return;
        }

        // 覆盖层贴在工作区左缘，全工作区高。
        _x = work.Left;
        _y = work.Top;
        _height = Math.Max(1, work.Bottom - work.Top);

        EnsureVisible();
        Render(progress, anchorY - work.Top, _height);
    }

    private void EnsureVisible()
    {
        // 定位 + 尺寸（不激活、保持置顶）。
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            _x,
            _y,
            _width,
            _height,
            NativeMethods.SWP_NOACTIVATE);

        if (!_visible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);
            _visible = true;
        }
    }

    public void Hide()
    {
        if (!_visible || _disposed)
        {
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _visible = false;
    }

    /// <summary>
    /// 用 GDI+ 渲染当前帧到 ARGB 位图并经 UpdateLayeredWindow 上屏。
    /// localAnchorY/height 为相对覆盖层左上角的坐标（物理像素）。
    /// </summary>
    private void Render(double progress, int localAnchorY, int height)
    {
        var p = Math.Clamp(progress, 0.0, 1.0);

        using var bitmap = new Bitmap(_width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 能量线水平居中于覆盖层内。
            float cx = _width / 2f;

            // 两条线的终点：上线向 0 延展，下线向 height 延展。
            float upEndY = (float)(localAnchorY - (p * localAnchorY));
            float downEndY = (float)(localAnchorY + (p * (height - localAnchorY)));

            // 辉光强度随进度增强。
            int coreAlpha = (int)(160 + (95 * p));
            int glowAlpha = (int)(40 + (110 * p));

            var coreColor = Color.FromArgb(Math.Clamp(coreAlpha, 0, 255), 120, 220, 255);
            var glowColor = Color.FromArgb(Math.Clamp(glowAlpha, 0, 255), 80, 180, 255);

            float glowWidth = (float)(10 + (14 * p));
            float coreWidth = (float)(2 + (2 * p));

            // 先画辉光（粗、半透明），再画核心高光（细、亮）。
            using (var glowPen = new Pen(glowColor, glowWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(glowPen, cx, localAnchorY, cx, upEndY);
                g.DrawLine(glowPen, cx, localAnchorY, cx, downEndY);
            }

            using (var corePen = new Pen(coreColor, coreWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(corePen, cx, localAnchorY, cx, upEndY);
                g.DrawLine(corePen, cx, localAnchorY, cx, downEndY);
            }

            // 锚点处一个亮核（光晕圆点），进度越大越亮越大。
            float dotR = (float)(4 + (6 * p));
            int dotAlpha = (int)(180 + (75 * p));
            using var dotBrush = new SolidBrush(Color.FromArgb(Math.Clamp(dotAlpha, 0, 255), 200, 240, 255));
            g.FillEllipse(dotBrush, cx - dotR, localAnchorY - dotR, dotR * 2, dotR * 2);

            // 推满时整条覆盖层来一次纵向高亮脉冲。
            if (p >= 0.999)
            {
                using var pulse = new SolidBrush(Color.FromArgb(70, 150, 220, 255));
                g.FillRectangle(pulse, 0, 0, _width, height);
            }
        }

        PushToLayeredWindow(bitmap, height);
    }

    private void PushToLayeredWindow(Bitmap bitmap, int height)
    {
        var screenDc = NativeMethods.GetDC(nint.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        try
        {
            var size = new NativeMethods.SIZE { cx = _width, cy = height };
            var srcPoint = new NativeMethods.POINT { X = 0, Y = 0 };
            var dstPoint = new NativeMethods.POINT { X = _x, Y = _y };
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

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        => NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }
}