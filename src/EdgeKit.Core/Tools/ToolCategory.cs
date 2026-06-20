namespace EdgeKit.Core.Tools;

/// <summary>
/// 工具分类，对应侧边菜单的分组。顺序即菜单分组的显示顺序。
/// 新增分类时在此追加，并在 <see cref="ToolCategoryInfo"/> 补充显示信息。
/// </summary>
public enum ToolCategory
{
    /// <summary>首页（最近窗口与常用工具）。</summary>
    Home,

    /// <summary>剪贴板（历史、搜索）。</summary>
    Clipboard,

    /// <summary>常用片段。</summary>
    Snippets,

    /// <summary>命令面板。</summary>
    Command,

    /// <summary>智能体。</summary>
    Agent,

    /// <summary>翻译。</summary>
    Translation,

    /// <summary>文本工具。</summary>
    Text,

    /// <summary>JSON 工具。</summary>
    Json,

    /// <summary>开发者工具（UUID、时间戳、Base64 等）。</summary>
    Developer,

    /// <summary>图片工具。</summary>
    Image,

    /// <summary>待办任务。</summary>
    Tasks,

    /// <summary>资源仪表盘。</summary>
    Dashboard,

    /// <summary>系统工具。</summary>
    System,

    /// <summary>设置中心。</summary>
    Settings
}

/// <summary>
/// 分类的显示信息（标题 + 图标），供菜单分组标题使用。
/// </summary>
public static class ToolCategoryInfo
{
    /// <summary>获取分类的中文显示标题。</summary>
    public static string GetTitle(ToolCategory category) => category switch
    {
        ToolCategory.Home => "首页",
        ToolCategory.Clipboard => "剪贴板",
        ToolCategory.Snippets => "常用片段",
        ToolCategory.Command => "命令面板",
        ToolCategory.Agent => "智能体",
        ToolCategory.Translation => "翻译",
        ToolCategory.Text => "文本工具",
        ToolCategory.Json => "JSON 工具",
        ToolCategory.Developer => "开发者工具",
        ToolCategory.Image => "图片工具",
        ToolCategory.Tasks => "待办任务",
        ToolCategory.Dashboard => "仪表盘",
        ToolCategory.System => "系统工具",
        ToolCategory.Settings => "设置",
        _ => category.ToString()
    };

    /// <summary>获取分类的图标字形（Segoe Fluent Icons），供可折叠分组父项显示。</summary>
    public static string GetGlyph(ToolCategory category) => category switch
    {
        ToolCategory.Home => "\uE80F",
        ToolCategory.Clipboard => "\uE8C8",
        ToolCategory.Snippets => "\uE7C3",
        ToolCategory.Command => "\uE756",
        ToolCategory.Agent => "\uE8D7",
        ToolCategory.Translation => "\uE9F3",
        ToolCategory.Text => "\uE8E9",
        ToolCategory.Json => "\uE943",
        ToolCategory.Developer => "\uEC7A",
        ToolCategory.Image => "\uEB9F",
        ToolCategory.Tasks => "\uE7C3",
        ToolCategory.Dashboard => "\uE9D2",
        ToolCategory.System => "\uE770",
        ToolCategory.Settings => "\uE713",
        _ => "\uE700"
    };
}
