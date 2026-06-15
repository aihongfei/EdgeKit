using EdgeKit.Core.Agent;

namespace EdgeKit.Services.Agent;

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly AgentConversationMode[] ChatAndWindows =
    {
        AgentConversationMode.Chat,
        AgentConversationMode.WindowsConfig
    };

    private static readonly AgentConversationMode[] WindowsOnly =
    {
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
        new("hosts_report", "Hosts 检查", "读取并校验 Windows hosts 文件。", AgentToolRisk.ReadOnly, WindowsOnly, false),
        new("env_report", "环境变量检查", "读取用户和系统环境变量摘要。", AgentToolRisk.ReadOnly, WindowsOnly, false),
        new("analyze_path", "Path 分析", "分析 PATH 环境变量中的空项、重复项和不存在路径。", AgentToolRisk.ReadOnly, WindowsOnly, false),
        new("file_lock_scan", "文件锁定扫描", "扫描指定文件或文件夹被哪些进程占用。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_json_format", "JSON 格式化", "格式化 JSON 文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_base64_decode", "Base64 解码", "解码 Base64 文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("text_url_decode", "URL 解码", "URL 解码文本。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("clipboard_search", "剪贴板历史查询", "查询最近剪贴板历史。", AgentToolRisk.ReadOnly, ChatAndWindows, false),
        new("open_windows_settings", "打开 Windows 设置", "打开指定的 Windows 设置页面。", AgentToolRisk.OpensExternal, WindowsOnly, true),
        new("flush_dns", "刷新 DNS", "刷新 Windows DNS 缓存。", AgentToolRisk.SystemWrite, WindowsOnly, true),
        new("save_hosts", "保存 hosts", "覆盖保存 Windows hosts 文件。", AgentToolRisk.SystemWrite, WindowsOnly, true),
        new("set_env", "设置环境变量", "设置用户或系统环境变量。", AgentToolRisk.SystemWrite, WindowsOnly, true),
        new("delete_env", "删除环境变量", "删除用户或系统环境变量。", AgentToolRisk.SystemWrite, WindowsOnly, true),
        new("file_lock_delete_recycle", "删除到回收站", "将指定文件或文件夹删除到回收站。", AgentToolRisk.Destructive, WindowsOnly, true),
        new("file_lock_kill_delete", "结束占用进程后删除", "结束指定占用进程后将目标删除到回收站。", AgentToolRisk.Destructive, WindowsOnly, true),
        new("kill_port_owner", "结束端口占用进程", "结束指定端口项对应的进程。", AgentToolRisk.Destructive, WindowsOnly, true)
    };

    public IReadOnlyList<AgentToolDescriptor> GetTools(AgentConversationMode mode, AgentSettings settings)
        => _tools
            .Where(t => t.Modes.Contains(mode))
            .Where(t => settings.AllowClipboardTools || t.Id != "clipboard_search")
            .ToArray();

    public AgentToolDescriptor? Find(string toolId)
        => _tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
}
