namespace EdgeKit.Core.Clipboard;

/// <summary>
/// 用户自定义的剪贴板分组。条目通过 <see cref="ClipboardItem.GroupId"/> 归属到某个分组，
/// 后续可由大模型自动归类到这些分组中。
/// </summary>
/// <param name="Id">数据库主键。</param>
/// <param name="Name">分组名称。</param>
/// <param name="ColorHex">分组角标颜色（如 "#4C8BF5"）；为空时用默认色。</param>
/// <param name="SortOrder">显示排序，越小越靠前。</param>
public sealed record ClipboardGroup(
    long Id,
    string Name,
    string ColorHex,
    int SortOrder);