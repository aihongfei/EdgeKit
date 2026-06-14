using System;

namespace EdgeKit.Core.QuickLaunch;

/// <summary>快速启动项类型。</summary>
public enum QuickLaunchItemKind
{
    /// <summary>应用，目标可以是 AppsFolder/AUMID，也可以是可执行文件路径。</summary>
    App,

    /// <summary>普通文件。</summary>
    File,

    /// <summary>文件夹。</summary>
    Folder
}

/// <summary>
/// 一条首页快速启动项。Target 为启动目标：应用解析名、文件路径或文件夹路径。
/// </summary>
public sealed record QuickLaunchItem(
    long Id,
    QuickLaunchItemKind Kind,
    string Title,
    string Target,
    int SortOrder,
    DateTime CreatedUtc);
