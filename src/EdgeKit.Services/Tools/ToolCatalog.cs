using EdgeKit.Core.Tools;

namespace EdgeKit.Services.Tools;

/// <summary>
/// 工具注册表的实现。所有工具集中在 <see cref="Registrations"/> 登记，
/// 菜单与内容区据此动态生成。
/// 新增工具：在 <see cref="Registrations"/> 追加一条 <see cref="ToolDescriptor"/> 即可，菜单自动出现。
/// </summary>
public sealed class ToolCatalog : IToolCatalog
{
    // 集中登记所有工具描述符。Glyph 取自 Segoe Fluent Icons。
    private static readonly ToolDescriptor[] Registrations =
    {
        // 首页
        new("home.dashboard", "首页", "\uE80F", ToolCategory.Home, 0, "最近窗口与常用工具"),

        // 剪贴板
        new("clipboard.history", "剪贴板历史", "\uE8C8", ToolCategory.Clipboard, 0, "查看与搜索剪贴板历史记录"),

        // 命令面板
        new("command.palette", "命令面板", "\uE756", ToolCategory.Command, 0, "快速搜索并执行系统命令"),

        // 文本工具
        new("text.tools", "文本工具", "\uE8E9", ToolCategory.Text, 0, "JSON 编辑器与 URL / Base64 编码转换"),
        new("text.encode", "编码转换", "\uE8C1", ToolCategory.Text, 1, "URL / Base64 编解码"),
        new("json.format", "JSON 格式化", "\uE943", ToolCategory.Text, 2, "格式化、压缩、校验 JSON"),
        new("json.tree", "JSON 预览", "\uE8A5", ToolCategory.Text, 3, "树形预览与路径复制"),

        // 图片工具
        new("image.convert", "图片转换", "\uEB9F", ToolCategory.Image, 0, "格式转换与压缩"),

        // 系统工具
        new("system.tools", "系统工具", "\uE770", ToolCategory.System, 0, "网络、端口、Hosts、环境变量、窗口管理"),
        new("system.filelock", "文件锁定检测", "\uE8A5", ToolCategory.System, 1, "检测文件或文件夹占用进程并处理锁定"),

        // 设置
        new("settings.center", "设置中心", "\uE713", ToolCategory.Settings, 0, "常规、抽屉、快捷键、隐私等设置"),
    };

    private readonly IReadOnlyList<ToolDescriptor> _all;
    private readonly IReadOnlyList<ToolCategoryGroup> _grouped;
    private readonly Dictionary<string, ToolDescriptor> _byId;

    public ToolCatalog()
    {
        _all = Registrations
            .OrderBy(t => (int)t.Category)
            .ThenBy(t => t.Order)
            .ToArray();

        _grouped = _all
            .GroupBy(t => t.Category)
            .OrderBy(g => (int)g.Key)
            .Select(g => new ToolCategoryGroup(
                g.Key,
                ToolCategoryInfo.GetTitle(g.Key),
                ToolCategoryInfo.GetGlyph(g.Key),
                g.OrderBy(t => t.Order).ToArray()))
            .ToArray();

        _byId = _all.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ToolDescriptor> GetAll() => _all;

    public IReadOnlyList<ToolCategoryGroup> GetGrouped() => _grouped;

    public ToolDescriptor? FindById(string id)
        => _byId.TryGetValue(id, out var d) ? d : null;
}
