using EdgeKit.Core.Agent;

namespace EdgeKit.Services.Agent;

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly AgentConversationMode[] ChatAndWindows =
    {
        AgentConversationMode.Chat,
        AgentConversationMode.WindowsConfig
    };

    private static readonly AgentConversationMode[] SystemToolModes =
    {
        AgentConversationMode.Chat,
        AgentConversationMode.WindowsConfig
    };

    private readonly AgentToolDescriptor[] _tools =
    {
        new("system_summary", "系统摘要", "读取当前 Windows 系统、CPU、内存、磁盘摘要。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("network_summary", "网络摘要", "读取网络适配器、公网 IP 与代理摘要。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("ports_list", "端口列表", "列出当前 TCP/UDP 端口和占用进程。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("ping_host", "Ping", "对指定主机执行 Ping 探测。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("resolve_dns", "DNS 解析", "解析指定主机名的 DNS 地址。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("probe_tcp", "TCP 探测", "测试指定主机和端口是否可连接。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("hosts_report", "Hosts 检查", "读取并校验 Windows hosts 文件。", AgentToolRisk.ReadOnly, SystemToolModes, false),
        new("env_report", "环境变量检查", "读取用户和系统环境变量摘要。", AgentToolRisk.ReadOnly, SystemToolModes, false),
        new("analyze_path", "Path 分析", "分析 PATH 环境变量中的空项、重复项和不存在路径。", AgentToolRisk.ReadOnly, SystemToolModes, false),
        new("file_lock_scan", "文件锁定扫描", "扫描指定文件或文件夹被哪些进程占用。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_json_format", "JSON 格式化", "格式化 JSON 文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_base64_decode", "Base64 解码", "解码 Base64 文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_url_decode", "URL 解码", "URL 解码文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("clipboard_search", "剪贴板历史查询", "查询最近剪贴板历史。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("file_read", "读取文件", "读取本机文本文件内容。参数: path, 可选 maxBytes。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("file_list", "列出文件", "列出指定目录下的文件和文件夹。参数: path, 可选 pattern, recursive, limit。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("file_search", "搜索文件内容", "在指定目录搜索文件名或文本内容。参数: path, query, 可选 pattern, recursive, limit。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("file_write", "写入文件", "创建、覆盖或追加写入文本文件；遇到权限不足时 EdgeKit 会统一请求管理员授权。参数: path, content, 可选 append。", AgentToolRisk.UserWrite, ChatAndWindows, true),
        new("file_patch", "替换文件文本", "在文本文件中执行精确字符串替换；遇到权限不足时 EdgeKit 会统一请求管理员授权。参数: path, oldText, newText。", AgentToolRisk.UserWrite, ChatAndWindows, true),
        new("file_delete_recycle", "文件删除到回收站", "将指定文件或文件夹删除到回收站。参数: path。", AgentToolRisk.Destructive, ChatAndWindows, true),
        new("shell_run", "运行 Shell 命令", "在 PowerShell 中执行命令并返回输出。参数: command, 可选 workingDirectory, timeoutSeconds, runAsAdministrator。管理员运行必须显式请求并确认。", AgentToolRisk.SystemWrite, ChatAndWindows, true),
        new("web_search", "Web 搜索", "调用已配置的搜索 API 查询网页结果。参数: query, 可选 limit。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("web_fetch", "读取网页", "读取指定 http/https URL 的文本内容。参数: url, 可选 maxBytes。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("open_windows_settings", "打开 Windows 设置", "打开指定的 Windows 设置页面。", AgentToolRisk.OpensExternal, SystemToolModes, true),
        new("flush_dns", "刷新 DNS", "刷新 Windows DNS 缓存。", AgentToolRisk.SystemWrite, SystemToolModes, true),
        new("save_hosts", "保存 hosts", "覆盖保存 Windows hosts 文件。需要完整 hosts 内容；普通权限下会触发管理员授权保存。", AgentToolRisk.SystemWrite, SystemToolModes, true),
        new("set_env", "设置环境变量", "设置用户或系统环境变量。", AgentToolRisk.SystemWrite, SystemToolModes, true),
        new("delete_env", "删除环境变量", "删除用户或系统环境变量。", AgentToolRisk.SystemWrite, SystemToolModes, true),
        new("file_lock_delete_recycle", "删除到回收站", "将指定文件或文件夹删除到回收站。", AgentToolRisk.Destructive, SystemToolModes, true),
        new("file_lock_kill_delete", "结束占用进程后删除", "结束指定占用进程后将目标删除到回收站。", AgentToolRisk.Destructive, SystemToolModes, true),
        new("kill_port_owner", "结束端口占用进程", "结束指定端口项对应的进程。", AgentToolRisk.Destructive, SystemToolModes, true)
    };

    public IReadOnlyList<AgentToolDescriptor> GetTools(AgentConversationMode mode, AgentSettings settings)
        => _tools
            .Where(t => t.Modes.Contains(mode))
            .Where(t => settings.AllowClipboardTools || t.Id != "clipboard_search")
            .Where(t => settings.EnableFileTools || !IsFileTool(t.Id))
            .Where(t => settings.EnableShellTools || t.Id != "shell_run")
            .Where(t => settings.EnableWebTools || !IsWebTool(t.Id))
            .ToArray();

    public AgentToolDescriptor? Find(string toolId)
        => _tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));

    private static bool IsFileTool(string id)
        => id.StartsWith("file_", StringComparison.OrdinalIgnoreCase)
            && id != "file_lock_scan"
            && id != "file_lock_delete_recycle"
            && id != "file_lock_kill_delete";

    private static bool IsWebTool(string id)
        => id is "web_search" or "web_fetch";
}
