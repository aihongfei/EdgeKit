using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 应用窗口图标提供器：按进程可执行文件路径提取图标并转为 WinUI 可用的 <see cref="BitmapImage"/>。
/// 基于 exe 路径而非 HWND，因此重启后仍可还原图标。结果按路径缓存到内存。
/// 取不到图标时返回 null，由 UI 降级为进程名首字母占位。
/// </summary>
internal static class WindowIconProvider
{
    // 路径 → 已解码图标。null 表示曾尝试但失败，避免重复尝试。
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取指定可执行文件的图标。无路径或提取失败返回 null。
    /// 必须在 UI 线程调用（创建 BitmapImage）。
    /// </summary>
    public static BitmapImage? GetIcon(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        return Cache.GetOrAdd(processPath, Extract);
    }

    private static BitmapImage? Extract(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.SetSource(stream.AsRandomAccessStream());
            return image;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "提取窗口图标失败 path={Path}", path);
            return null;
        }
    }
}