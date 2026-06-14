namespace EdgeKit.Core.Tools;

/// <summary>
/// 工具元数据描述符。菜单与内容区均由它驱动，新增工具只需提供一条描述符。
/// </summary>
/// <param name="Id">工具唯一标识（用于导航、设置持久化、收藏等）。</param>
/// <param name="Title">显示名称。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形码（如 "\uE77F"）。</param>
/// <param name="Category">所属分类（决定菜单分组）。</param>
/// <param name="Order">同分类内的排序值，升序。</param>
/// <param name="Description">简短说明，占位页与提示使用。</param>
public sealed record ToolDescriptor(
    string Id,
    string Title,
    string Glyph,
    ToolCategory Category,
    int Order = 0,
    string Description = "");