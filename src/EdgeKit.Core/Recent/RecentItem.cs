namespace EdgeKit.Core.Recent;

/// <summary>
/// 最近项的类型：最近使用的工具，或最近活动的应用窗口。
/// </summary>
public enum RecentItemKind
{
    /// <summary>最近使用过的工具。</summary>
    Tool,

    /// <summary>最近活动的应用窗口（Alt+Tab 列表）。</summary>
    Window,

    /// <summary>从搜索列表启动的应用。</summary>
    App,

    /// <summary>命令面板执行过的命令。</summary>
    Command
}

/// <summary>
/// 一条最近使用记录。工具与窗口共用此结构，由 <see cref="Kind"/> 区分。
/// </summary>
/// <param name="Kind">记录类型。</param>
/// <param name="Key">
/// 唯一标识：工具为工具 Id；窗口为"进程路径|窗口标题"组合，便于重启后重新拉起。
/// </param>
/// <param name="Title">显示标题（工具名 / 窗口标题）。</param>
/// <param name="SubTitle">副标题（工具分类 / 进程名或路径）。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形码，无图标时的占位用。</param>
/// <param name="LastUsedUtc">最后一次使用时间（UTC，ISO 8601）。</param>
/// <param name="UseCount">累计使用次数。</param>
public sealed record RecentItem(
    RecentItemKind Kind,
    string Key,
    string Title,
    string SubTitle,
    string Glyph,
    DateTime LastUsedUtc,
    int UseCount = 1);
