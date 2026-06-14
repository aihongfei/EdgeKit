using System.Collections.Generic;
using EdgeKit.Core.Tools;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 抽屉外壳的 ViewModel。提供分组后的菜单数据与当前选中工具，
/// 菜单完全由 <see cref="IToolCatalog"/> 驱动。
/// </summary>
public sealed class DrawerShellViewModel
{
    private readonly IToolCatalog _catalog;

    /// <summary>分组后的菜单数据，供侧边菜单渲染。</summary>
    public IReadOnlyList<ToolCategoryGroup> Groups { get; }

    /// <summary>当前选中的工具，内容区据此切换。</summary>
    public ToolDescriptor? SelectedTool { get; private set; }

    public DrawerShellViewModel(IToolCatalog catalog)
    {
        _catalog = catalog;
        Groups = _catalog.GetGrouped();

        // 默认选中第一个工具。
        var all = _catalog.GetAll();
        if (all.Count > 0)
        {
            SelectedTool = all[0];
        }
    }

    /// <summary>按工具 Id 选中（供菜单选中事件调用）。</summary>
    public void SelectToolById(string id)
    {
        var tool = _catalog.FindById(id);
        if (tool is not null)
        {
            SelectedTool = tool;
        }
    }
}