using System.Collections.Generic;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgeKit.App.Windows.Navigation;

/// <summary>
/// 根据工具注册表动态构建侧边菜单：每个分类一个可折叠父项，下属工具为二级菜单项。
/// 加工具只需在 ToolCatalog 注册，无需改窗口代码。
/// </summary>
internal static class ToolNavigationBuilder
{
    /// <summary>构建菜单的结果：菜单项到工具 Id 的映射 + 默认选中的首个工具项。</summary>
    public readonly record struct BuildResult(
        IReadOnlyDictionary<NavigationViewItem, string> ItemToolMap,
        NavigationViewItem? FirstTool);

    /// <summary>
    /// 把分组数据填充进指定 NavigationView，返回菜单项映射与首个工具项。
    /// </summary>
    public static BuildResult Build(NavigationView nav, DrawerShellViewModel shellViewModel)
    {
        var map = new Dictionary<NavigationViewItem, string>();
        NavigationViewItem? firstTool = null;

        foreach (var group in shellViewModel.Groups)
        {
            // 首页、剪贴板历史、命令面板、智能体、文本工具、图片工具、系统工具、设置：渲染为单个顶级可点击项。
            if (ShouldRenderAsTopLevel(group.Category) && group.Tools.Count > 0)
            {
                var tool = group.Tools[0];
                var topLevelItem = new NavigationViewItem
                {
                    Content = group.Category is ToolCategory.Clipboard or ToolCategory.Image ? tool.Title : group.Title,
                    Icon = new FontIcon { Glyph = group.Glyph },
                    Tag = tool.Id,
                    SelectsOnInvoked = true
                };
                map[topLevelItem] = tool.Id;
                firstTool ??= topLevelItem;
                nav.MenuItems.Add(topLevelItem);
                continue;
            }

            // 分类做成可折叠的父项：本身不导航，点击只展开/折叠二级菜单。
            // 收缩态点击父项时，NavigationView 原生会弹出该分类下的二级菜单。
            var parent = new NavigationViewItem
            {
                Content = group.Title,
                Icon = new FontIcon { Glyph = group.Glyph },
                SelectsOnInvoked = false,
                IsExpanded = false
            };

            foreach (var tool in group.Tools)
            {
                var item = new NavigationViewItem
                {
                    Content = tool.Title,
                    Icon = new FontIcon { Glyph = tool.Glyph },
                    Tag = tool.Id,
                    // 二级子项默认缩进较大，使选中竖线偏右、与高亮块脱节；
                    // 用较小的负左边距整体左移一点，兼顾两侧留白不至于过左。
                    Margin = new Thickness(-6, 0, 0, 0)
                };
                map[item] = tool.Id;
                parent.MenuItems.Add(item);

                firstTool ??= item;
            }

            nav.MenuItems.Add(parent);
        }

        return new BuildResult(map, firstTool);
    }

    private static bool ShouldRenderAsTopLevel(ToolCategory category)
        => category is ToolCategory.Home or ToolCategory.Clipboard or ToolCategory.Command or ToolCategory.Agent or ToolCategory.Translation or ToolCategory.Text or ToolCategory.Image or ToolCategory.System or ToolCategory.Settings;
}
