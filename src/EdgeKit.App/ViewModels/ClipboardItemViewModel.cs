using System;
using System.IO;
using EdgeKit.Core.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 剪贴板历史卡片的展示模型。包裹一条 <see cref="ClipboardItem"/>，
/// 附加类型角标文案/图标、图片缩略图等 UI 展示所需信息。
/// </summary>
public sealed class ClipboardItemViewModel
{
    public ClipboardItemViewModel(ClipboardItem model)
    {
        Model = model;

        var imagePath = GetPreviewImagePath(model);
        if (!string.IsNullOrEmpty(imagePath))
        {
            Thumbnail = new BitmapImage(new Uri(imagePath));
        }
    }

    /// <summary>底层数据。</summary>
    public ClipboardItem Model { get; }

    /// <summary>数据库主键。</summary>
    public long Id => Model.Id;

    /// <summary>摘要文本（卡片正文）。</summary>
    public string Preview => Model.Preview;

    /// <summary>悬浮预览用的完整内容。</summary>
    public string FullContent
    {
        get
        {
            if (Model.Files is { Count: > 0 })
            {
                return string.Join(Environment.NewLine, Model.Files);
            }

            if (!string.IsNullOrWhiteSpace(Model.Content))
            {
                return Model.Content;
            }

            if (!string.IsNullOrWhiteSpace(Model.ImagePath))
            {
                return Model.ImagePath;
            }

            return Model.Preview;
        }
    }

    /// <summary>图片缩略图；非图片为 null。</summary>
    public BitmapImage? Thumbnail { get; }

    /// <summary>是否为图片项（用于卡片模板可见性切换）。</summary>
    public bool IsImage => Thumbnail is not null;

    /// <summary>是否为文本类项（图片之外都按文本展示摘要）。</summary>
    public bool IsText => !IsImage;

    /// <summary>是否已固定。</summary>
    public bool Pinned => Model.Pinned;

    /// <summary>复制来源应用名。</summary>
    public string SourceAppName => string.IsNullOrWhiteSpace(Model.SourceAppName)
        ? string.Empty
        : Model.SourceAppName;

    /// <summary>是否显示来源应用标签。</summary>
    public Visibility SourceVisibility =>
        string.IsNullOrWhiteSpace(SourceAppName) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>复制时间（本地时间）的相对/友好文案。</summary>
    public string TimeLabel
    {
        get
        {
            var local = Model.CreatedUtc.ToLocalTime();
            var now = DateTime.Now;
            var diff = now - local;

            if (diff.TotalMinutes < 1)
            {
                return "刚刚";
            }

            if (diff.TotalHours < 1)
            {
                return $"{diff.TotalMinutes:0} 分钟前";
            }

            if (local.Date == now.Date)
            {
                return $"今天 {local:HH:mm}";
            }

            if (local.Date == now.Date.AddDays(-1))
            {
                return $"昨天 {local:HH:mm}";
            }

            return local.ToString("MM-dd HH:mm");
        }
    }

    /// <summary>复制时间的完整绝对时间文案，供 ToolTip 使用。</summary>
    public string AbsoluteTimeLabel => Model.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>类型角标文案。</summary>
    public string KindLabel => Model.Kind switch
    {
        _ when IsImage => "图片",
        ClipboardItemKind.Text => "文本",
        ClipboardItemKind.Url => "链接",
        ClipboardItemKind.Json => "JSON",
        ClipboardItemKind.FilePath => "路径",
        ClipboardItemKind.Image => "图片",
        ClipboardItemKind.Files => "文件",
        _ => "文本"
    };

    /// <summary>类型角标图标（Segoe Fluent Icons）。</summary>
    public string KindGlyph => Model.Kind switch
    {
        _ when IsImage => "\uEB9F",
        ClipboardItemKind.Text => "\uE8E9",
        ClipboardItemKind.Url => "\uE71B",
        ClipboardItemKind.Json => "\uE943",
        ClipboardItemKind.FilePath => "\uE8E5",
        ClipboardItemKind.Image => "\uEB9F",
        ClipboardItemKind.Files => "\uE8B7",
        _ => "\uE8E9"
    };

    private static string? GetPreviewImagePath(ClipboardItem model)
    {
        if (model.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(model.ImagePath))
        {
            return model.ImagePath;
        }

        if (model.Kind == ClipboardItemKind.FilePath && LooksLikeImageFile(model.Content))
        {
            return model.Content;
        }

        return null;
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
