namespace EdgeKit.Core.Tools;

/// <summary>
/// 工具契约。每个具体工具实现此接口，提供自己的描述符。
/// 这一轮菜单主要由 <see cref="IToolCatalog"/> 的描述符列表驱动；
/// 后续工具落地时实现本接口，并可扩展执行/视图相关成员。
/// </summary>
public interface ITool
{
    /// <summary>工具元数据。</summary>
    ToolDescriptor Descriptor { get; }
}