using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace EdgeKit.App.Search;

/// <summary>
/// 把 <c>IShellItemImageFactory.GetImage</c> 返回的 32bpp <c>HBITMAP</c>
/// 转为 WinUI 可用的 <see cref="BitmapImage"/>。
/// 不能用 <c>System.Drawing.Image.FromHbitmap</c>：它会忽略 alpha 通道，
/// 把透明区域填黑（图标四周出现黑边）。这里改用 <c>GetDIBits</c> 取出像素并
/// 还原预乘 alpha，得到正确的透明背景。
/// 必须在 UI 线程调用（创建 BitmapImage）。调用方负责释放 HBITMAP。
/// </summary>
internal static class IconConverter
{
    public static BitmapImage? FromHBitmap(nint hbitmap)
    {
        if (hbitmap == nint.Zero)
        {
            return null;
        }

        try
        {
            using var bitmap = BuildArgbBitmap(hbitmap);
            if (bitmap is null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.SetSource(stream.AsRandomAccessStream());
            return image;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HBITMAP 转 BitmapImage 失败");
            return null;
        }
    }

    /// <summary>
    /// 用 <c>GetDIBits</c> 以 top-down 32bpp 取出位图像素，保留并还原 alpha 通道。
    /// </summary>
    private static Bitmap? BuildArgbBitmap(nint hbitmap)
    {
        var bm = default(BITMAP);
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bm) == 0)
        {
            return null;
        }

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 负高度 = top-down，行序与 Bitmap 一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0 // BI_RGB
        };

        var stride = width * 4;
        var pixels = new byte[stride * height];

        var hdc = GetDC(nint.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(nint.Zero, hdc);
        }

        NormalizeAlpha(pixels);

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    /// <summary>
    /// 像素为 BGRA 顺序。若 alpha 全 0（图标无 alpha 通道）则整体置为不透明，
    /// 避免整图透明；否则把预乘 alpha 还原为直通 alpha，消除半透明边缘偏暗。
    /// </summary>
    private static void NormalizeAlpha(byte[] pixels)
    {
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return;
        }

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
            }
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint hdc, nint hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}