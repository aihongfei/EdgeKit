using System;
using System.Collections.Generic;
using System.Linq;
using EdgeKit.App.Search;
using EdgeKit.Core.Commands;

namespace EdgeKit.App.Commands;

/// <summary>合并内置命令与用户自定义命令，并提供搜索能力。</summary>
public sealed class CommandRegistry
{
    private readonly ICustomCommandRepository _customCommands;

    public CommandRegistry(ICustomCommandRepository customCommands)
    {
        _customCommands = customCommands;
        _customCommands.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CommandDescriptor> GetAll()
        => BuiltInCommands
            .Concat(_customCommands.GetAll().Select(FromCustomCommand))
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public CommandDescriptor? FindById(string id)
        => GetAll().FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CommandDescriptor> Search(string query, int maxResults = 80)
    {
        var commands = GetAll();
        if (string.IsNullOrWhiteSpace(query))
        {
            return commands.Take(maxResults).ToList();
        }

        var q = query.Trim().ToLowerInvariant();
        return commands
            .Select(c => (Command: c, Score: new SearchTextIndex(c.Title, c.Description, c.Keywords).Match(q)))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Command.Order)
            .ThenBy(s => s.Command.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Command.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(s => s.Command)
            .ToList();
    }

    private static CommandDescriptor FromCustomCommand(CustomCommand command)
        => new(
            $"custom:{command.Id}",
            command.Title,
            string.IsNullOrWhiteSpace(command.CommandText) ? "自定义命令" : command.CommandText,
            string.IsNullOrWhiteSpace(command.Glyph) ? "\uE756" : command.Glyph,
            "自定义命令",
            CommandKind.Custom,
            command.CommandText,
            command.Arguments,
            command.WorkingDirectory,
            SplitKeywords(command.Keywords),
            10000 + (int)Math.Min(command.Id, int.MaxValue - 10000),
            command.RequiresConfirmation,
            command.RunAsAdministrator,
            IsCustom: true,
            CustomCommandId: command.Id);

    private static IReadOnlyList<string> SplitKeywords(string keywords)
        => (keywords ?? string.Empty)
            .Split(new[] { ',', ';', '，', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static CommandDescriptor C(
        string id,
        string title,
        string description,
        string glyph,
        string category,
        CommandKind kind,
        string target,
        string arguments,
        int order,
        bool requiresConfirmation = false,
        params string[] keywords)
        => new(
            id,
            title,
            description,
            glyph,
            category,
            kind,
            target,
            arguments,
            string.Empty,
            keywords,
            order,
            requiresConfirmation);

    private static readonly CommandDescriptor[] BuiltInCommands =
    {
        C("windows.settings.display", "显示设置", "打开 Windows 显示设置", "\uE7F4", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:display", "", 100, false, "display", "xssz", "xs"),
        C("windows.settings.sound", "声音设置", "打开 Windows 声音设置", "\uE767", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:sound", "", 101, false, "sound", "sy"),
        C("windows.settings.network", "网络设置", "打开 Windows 网络设置", "\uE968", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:network", "", 102, false, "network", "wl"),
        C("windows.settings.bluetooth", "蓝牙设置", "打开 Windows 蓝牙设置", "\uE702", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:bluetooth", "", 103, false, "bluetooth", "ly"),
        C("windows.settings.apps", "应用设置", "打开应用和功能设置", "\uECAA", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:appsfeatures", "", 104, false, "apps", "yy"),
        C("windows.settings.defaultapps", "默认应用", "打开默认应用设置", "\uE71B", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:defaultapps", "", 105, false, "default", "mryy"),
        C("windows.settings.startupapps", "启动应用", "打开启动应用设置", "\uE7C3", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:startupapps", "", 106, false, "startup", "qdy"),
        C("windows.settings.privacy", "隐私设置", "打开 Windows 隐私设置", "\uE72E", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:privacy", "", 107, false, "privacy", "ys"),
        C("windows.settings.power", "电源和睡眠", "打开电源和睡眠设置", "\uE7E8", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:powersleep", "", 108, false, "power", "dy", "sleep"),
        C("windows.settings.battery", "电池设置", "打开电池设置", "\uE850", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:batterysaver", "", 109, false, "battery", "dc"),
        C("windows.settings.update", "Windows 更新", "打开 Windows 更新", "\uE895", "Windows 设置",
            CommandKind.WindowsUri, "ms-settings:windowsupdate", "", 110, false, "update", "gx"),

        C("system.taskmgr", "任务管理器", "打开任务管理器", "\uE9D9", "系统工具",
            CommandKind.Process, "taskmgr.exe", "", 200, false, "taskmgr", "task manager", "rwglq"),
        C("system.control", "控制面板", "打开经典控制面板", "\uE713", "系统工具",
            CommandKind.Process, "control.exe", "", 201, false, "control", "kzmb"),
        C("system.network.connections", "网络连接", "打开网络连接控制面板", "\uE968", "系统工具",
            CommandKind.Process, "ncpa.cpl", "", 202, false, "network connections", "wllj", "ncpa"),
        C("system.device.manager", "设备管理器", "打开设备管理器", "\uE772", "系统工具",
            CommandKind.Process, "devmgmt.msc", "", 203, false, "device manager", "sbglq"),
        C("system.disk.management", "磁盘管理", "打开磁盘管理", "\uEDA2", "系统工具",
            CommandKind.Process, "diskmgmt.msc", "", 204, false, "disk", "cpgl"),
        C("system.services", "服务", "打开 Windows 服务管理器", "\uE8D4", "系统工具",
            CommandKind.Process, "services.msc", "", 205, false, "services", "fw"),
        C("system.regedit", "注册表编辑器", "打开注册表编辑器", "\uE7C3", "系统工具",
            CommandKind.Process, "regedit.exe", "", 206, false, "regedit", "zcb"),
        C("system.terminal", "Windows 终端", "打开 Windows Terminal", "\uE756", "系统工具",
            CommandKind.Process, "wt.exe", "", 207, false, "terminal", "cmd", "powershell", "zd"),
        C("system.explorer", "文件资源管理器", "打开文件资源管理器", "\uE8B7", "系统工具",
            CommandKind.Process, "explorer.exe", "", 208, false, "explorer", "wjzyglq"),
        C("system.filelock", "文件锁定检测", "检测文件或文件夹被哪个进程占用", "\uE8A5", "系统工具",
            CommandKind.BuiltInAction, "navigate:system.filelock", "", 209, false, "file lock", "locked file", "unlocker", "wjzd", "wjzy"),

        C("agent.chat", "打开智能体", "打开 EdgeKit 智能体对话", "\uE8D7", "智能体",
            CommandKind.BuiltInAction, "navigate:agent.chat", "", 250, false, "ai", "agent", "chat", "assistant", "znt"),

        C("shell.desktop", "桌面", "打开桌面文件夹", "\uE80F", "Shell 入口",
            CommandKind.WindowsUri, "shell:Desktop", "", 300, false, "desktop", "zm"),
        C("shell.downloads", "下载", "打开下载文件夹", "\uE896", "Shell 入口",
            CommandKind.WindowsUri, "shell:Downloads", "", 301, false, "downloads", "xz"),
        C("shell.documents", "文档", "打开文档文件夹", "\uE8A5", "Shell 入口",
            CommandKind.WindowsUri, "shell:Personal", "", 302, false, "documents", "wd"),
        C("shell.startup", "启动文件夹", "打开当前用户启动文件夹", "\uE7C3", "Shell 入口",
            CommandKind.WindowsUri, "shell:Startup", "", 303, false, "startup folder", "qdwj"),
        C("shell.recyclebin", "回收站", "打开回收站", "\uE74D", "Shell 入口",
            CommandKind.WindowsUri, "shell:RecycleBinFolder", "", 304, false, "recycle", "hsz"),
        C("shell.appsfolder", "应用文件夹", "打开 Windows 应用文件夹", "\uECAA", "Shell 入口",
            CommandKind.WindowsUri, "shell:AppsFolder", "", 305, false, "apps folder", "yywj"),

        C("system.lock", "锁屏", "锁定当前 Windows 会话", "\uE72E", "系统动作",
            CommandKind.Process, "rundll32.exe", "user32.dll,LockWorkStation", 400, true, "lock", "sp"),
        C("system.logoff", "注销", "注销当前 Windows 用户", "\uE8AC", "系统动作",
            CommandKind.Process, "shutdown.exe", "/l", 401, true, "logoff", "zx"),
        C("system.shutdown", "关机", "关闭电脑", "\uE7E8", "系统动作",
            CommandKind.Process, "shutdown.exe", "/s /t 0", 402, true, "shutdown", "gj"),
        C("system.restart", "重启", "重启电脑", "\uE777", "系统动作",
            CommandKind.Process, "shutdown.exe", "/r /t 0", 403, true, "restart", "cq"),
        C("system.sleep", "睡眠", "让电脑进入睡眠", "\uE708", "系统动作",
            CommandKind.Process, "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0", 404, true, "sleep", "sm")
    };
}
