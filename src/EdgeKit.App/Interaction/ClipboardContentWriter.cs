using System;
using System.IO;
using EdgeKit.Core.Clipboard;
using EdgeKit.Services.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EdgeKit.App.Interaction;

/// <summary>
/// 把历史条目写回系统剪贴板。首页与剪贴板历史页共用，避免图片项退化为路径文本。
/// </summary>
public sealed class ClipboardContentWriter
{
    private readonly ClipboardService _clipboardService;

    public ClipboardContentWriter(ClipboardService clipboardService)
    {
        _clipboardService = clipboardService;
    }

    public void Copy(ClipboardItem item)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

        switch (item.Kind)
        {
            case ClipboardItemKind.Image when !string.IsNullOrEmpty(item.ImagePath):
                var file = StorageFile.GetFileFromPathAsync(item.ImagePath).AsTask().GetAwaiter().GetResult();
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                break;
            case ClipboardItemKind.FilePath when LooksLikeImageFile(item.Content):
                var imageFile = StorageFile.GetFileFromPathAsync(item.Content).AsTask().GetAwaiter().GetResult();
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(imageFile));
                break;
            case ClipboardItemKind.Files when item.Files is { Count: > 0 }:
                package.SetText(string.Join(Environment.NewLine, item.Files));
                break;
            default:
                package.SetText(item.Content);
                break;
        }

        _clipboardService.SuppressNextCapture();
        Clipboard.SetContent(package);
    }

    private static bool LooksLikeImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }
}
