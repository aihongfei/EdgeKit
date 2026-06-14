namespace EdgeKit.Core.Tools;

/// <summary>
/// 工具注册表。集中提供所有工具的描述符，菜单据此动态生成。
/// </summary>
public interface IToolCatalog
{
    /// <summary>获取全部工具描述符（已按分类与 Order 排序）。</summary>
    IReadOnlyList<ToolDescriptor> GetAll();

    /// <summary>按分类分组获取工具，分组顺序与分类枚举顺序一致。</summary>
    IReadOnlyList<ToolCategoryGroup> GetGrouped();

    /// <summary>按 Id 查找工具描述符，未找到返回 null。</summary>
    ToolDescriptor? FindById(string id);
}

/// <summary>
/// 一个分类及其下属工具，供菜单分组渲染。
/// </summary>
/// <param name="Category">分类。</param>
/// <param name="Title">分类显示标题。</param>
/// <param name="Glyph">分类图标字形（Segoe Fluent Icons）。</param>
/// <param name="Tools">该分类下的工具（已排序）。</param>
public sealed record ToolCategoryGroup(
    ToolCategory Category,
    string Title,
    string Glyph,
    IReadOnlyList<ToolDescriptor> Tools);