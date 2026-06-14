using System;
using System.Collections.Generic;

namespace EdgeKit.Core.Clipboard;

/// <summary>
/// 剪贴板条目的内容类型。由系统在捕获时自动识别，作为内容标签使用，
/// 与用户自定义的分组（<see cref="ClipboardItem.GroupId"/>）相互独立。
/// </summary>
public enum ClipboardItemKind
{
    /// <summary>纯文本。</summary>
    Text,

    /// <summary>URL 链接。</summary>
    Url,

    /// <summary>JSON 文本。</summary>
    Json,

    /// <summary>单条文件路径文本。</summary>
    FilePath,

    /// <summary>图片（正文存为本地文件，库中记 <see cref="ClipboardItem.ImagePath"/>）。</summary>
    Image,

    /// <summary>文件列表（来自资源管理器复制的多个文件/文件夹）。</summary>
    Files
}

/// <summary>
/// 一条剪贴板历史记录。文本/URL/JSON/文件路径正文存 <see cref="Content"/>；
/// 图片正文落盘后由 <see cref="ImagePath"/> 引用；多文件列表存 <see cref="Files"/>。
/// </summary>
/// <param name="Id">数据库主键；新建未入库前为 0。</param>
/// <param name="Kind">系统识别的内容类型。</param>
/// <param name="Preview">列表展示用的摘要文本（截断后的内容或文件名）。</param>
/// <param name="Content">正文：文本/URL/JSON/单条路径；图片与文件列表此项可为空串。</param>
/// <param name="ImagePath">图片本地文件绝对路径；非图片为 null。</param>
/// <param name="Files">文件列表；非文件列表为 null。</param>
/// <param name="GroupId">用户自定义分组 Id；未分组为 null（后续可由大模型回填）。</param>
/// <param name="Pinned">是否固定（固定项不参与条数裁剪）。</param>
/// <param name="SourceAppName">复制来源应用名；未知时为空串。</param>
/// <param name="SourceProcessPath">复制来源进程路径；未知时为空串。</param>
/// <param name="Hash">内容指纹，用于去重。</param>
/// <param name="CreatedUtc">复制/最近一次出现时间（UTC）。</param>
public sealed record ClipboardItem(
    long Id,
    ClipboardItemKind Kind,
    string Preview,
    string Content,
    string? ImagePath,
    IReadOnlyList<string>? Files,
    long? GroupId,
    bool Pinned,
    string SourceAppName,
    string SourceProcessPath,
    string Hash,
    DateTime CreatedUtc);
