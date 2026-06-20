namespace EdgeKit.Core.Services;
using EdgeKit.Core.Agent;

/// <summary>单次触发来自的屏幕边缘。</summary>
public enum EdgeTriggerSide
{
    Left,
    Right
}

/// <summary>允许响应的屏幕边缘。</summary>
[Flags]
public enum EdgeTriggerSides
{
    Left = 1,
    Right = 2,
    Both = Left | Right
}

/// <summary>
/// 应用设置的读取与更新能力。底层通过 <c>ISettingsStore</c> 持久化到 SQLite。
/// </summary>
public interface ISettingsService
{
    /// <summary>设置发生变更（属性赋值或 <see cref="Save"/>）后触发，供 UI 实时响应（如抽屉宽度）。</summary>
    event EventHandler? Changed;

    /// <summary>抽屉宽度（像素）。</summary>
    int DrawerWidth { get; set; }

    /// <summary>允许显示边缘呼出长条的屏幕边缘。</summary>
    EdgeTriggerSides TriggerSides { get; set; }

    /// <summary>边缘呼出长条宽度（像素）。</summary>
    int EdgeHandleWidth { get; set; }

    /// <summary>边缘呼出长条高度（像素）。运行时还会按当前屏幕工作区高度限制。</summary>
    int EdgeHandleHeight { get; set; }

    /// <summary>自动收起延迟（毫秒）。光标离开抽屉后等待此时长再收起。</summary>
    int AutoHideDelayMs { get; set; }

    /// <summary>滑入/滑出动画时长（毫秒）。</summary>
    int AnimationDurationMs { get; set; }

    /// <summary>全屏应用中禁用抽屉。</summary>
    bool DisableInFullscreen { get; set; }

    /// <summary>侧边菜单默认展开（true）或收缩为图标窄条（false）。</summary>
    bool MenuExpanded { get; set; }

    /// <summary>打开抽屉并聚焦搜索框的全局快捷键。</summary>
    string GlobalHotkeyText { get; set; }

    /// <summary>搜索框默认是否启用 Everything 搜索。</summary>
    bool UseEverythingSearch { get; set; }

    /// <summary>是否记录最近活动的应用窗口（首页展示用）。涉及隐私，可关闭。</summary>
    bool TrackRecentWindows { get; set; }

    /// <summary>是否记录最近使用过的工具（首页展示用）。</summary>
    bool TrackRecentTools { get; set; }

    /// <summary>首页展示的最近活动窗口最大条数。</summary>
    int RecentWindowsDisplayLimit { get; set; }

    /// <summary>首页最近使用区可见行数。</summary>
    int RecentToolsVisibleRows { get; set; }

    /// <summary>首页是否展示最近剪贴板历史。</summary>
    bool ShowHomeClipboardHistory { get; set; }

    /// <summary>首页快速启动区可见行数。</summary>
    int QuickLaunchVisibleRows { get; set; }

    bool AiEnabled { get; set; }

    string AiBaseUrl { get; set; }

    string AiModel { get; set; }

    string AiApiKeyEncrypted { get; set; }

    string AiApiKeyPreview { get; set; }

    double AiTemperature { get; set; }

    AgentConversationMode AiDefaultMode { get; set; }

    AgentActionMode AiActionMode { get; set; }

    bool AiAllowClipboardTools { get; set; }

    bool AiEnableFileTools { get; set; }

    bool AiEnableShellTools { get; set; }

    bool AiEnableWebTools { get; set; }

    bool AiEnableMcpTools { get; set; }

    AgentSearchProvider AiSearchProvider { get; set; }

    string AiSearchApiKeyEncrypted { get; set; }

    string AiSearchApiKeyPreview { get; set; }

    string AiTrustedDirectories { get; set; }

    string AiShellCommandWhitelist { get; set; }

    string AiMcpServersJson { get; set; }

    int AiContextWindowTokens { get; set; }

    string YoudaoAppKeyEncrypted { get; set; }

    string YoudaoAppKeyPreview { get; set; }

    string YoudaoAppSecretEncrypted { get; set; }

    string YoudaoAppSecretPreview { get; set; }

    string SyncfusionLicenseKeyEncrypted { get; set; }

    string SyncfusionLicenseKeyPreview { get; set; }

    /// <summary>加载持久化的设置。</summary>
    void Load();

    /// <summary>保存当前设置。</summary>
    void Save();
}
