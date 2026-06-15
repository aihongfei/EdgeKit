using System.Globalization;
using EdgeKit.Core.Agent;
using EdgeKit.Core.Services;
using EdgeKit.Core.Settings;

namespace EdgeKit.Services.Settings;

/// <summary>
/// 设置服务。强类型属性与底层键值存储（<see cref="ISettingsStore"/>）之间做映射，
/// Load 从存储读取，Save 写回；任意属性变更或 Save 后触发 <see cref="Changed"/>。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly ISettingsStore _store;

    public SettingsService(ISettingsStore store)
    {
        _store = store;
    }

    public event EventHandler? Changed;

    private int _drawerWidth = 480;
    public int DrawerWidth
    {
        get => _drawerWidth;
        set => SetField(ref _drawerWidth, value);
    }

    private EdgeTriggerSides _triggerSides = EdgeTriggerSides.Left;
    public EdgeTriggerSides TriggerSides
    {
        get => _triggerSides;
        set => SetField(ref _triggerSides, NormalizeTriggerSides(value));
    }

    private int _edgeHandleWidth = 24;
    public int EdgeHandleWidth
    {
        get => _edgeHandleWidth;
        set => SetField(ref _edgeHandleWidth, ClampEdgeHandleWidth(value));
    }

    private int _edgeHandleHeight = 580;
    public int EdgeHandleHeight
    {
        get => _edgeHandleHeight;
        set => SetField(ref _edgeHandleHeight, ClampEdgeHandleHeight(value));
    }

    private int _autoHideDelayMs = 400;
    public int AutoHideDelayMs
    {
        get => _autoHideDelayMs;
        set => SetField(ref _autoHideDelayMs, value);
    }

    private int _animationDurationMs = 180;
    public int AnimationDurationMs
    {
        get => _animationDurationMs;
        set => SetField(ref _animationDurationMs, value);
    }

    private bool _disableInFullscreen = true;
    public bool DisableInFullscreen
    {
        get => _disableInFullscreen;
        set => SetField(ref _disableInFullscreen, value);
    }

    private bool _menuExpanded;
    public bool MenuExpanded
    {
        get => _menuExpanded;
        set => SetField(ref _menuExpanded, value);
    }

    private string _globalHotkeyText = "Ctrl+Alt+K";
    public string GlobalHotkeyText
    {
        get => _globalHotkeyText;
        set => SetField(ref _globalHotkeyText, NormalizeHotkeyText(value));
    }

    private bool _useEverythingSearch;
    public bool UseEverythingSearch
    {
        get => _useEverythingSearch;
        set => SetField(ref _useEverythingSearch, value);
    }

    private bool _trackRecentWindows = true;
    public bool TrackRecentWindows
    {
        get => _trackRecentWindows;
        set => SetField(ref _trackRecentWindows, value);
    }

    private bool _trackRecentTools = true;
    public bool TrackRecentTools
    {
        get => _trackRecentTools;
        set => SetField(ref _trackRecentTools, value);
    }

    private int _recentWindowsDisplayLimit = 12;
    public int RecentWindowsDisplayLimit
    {
        get => _recentWindowsDisplayLimit;
        set => SetField(ref _recentWindowsDisplayLimit, ClampRecentDisplayLimit(value));
    }

    private int _recentToolsVisibleRows = 2;
    public int RecentToolsVisibleRows
    {
        get => _recentToolsVisibleRows;
        set => SetField(ref _recentToolsVisibleRows, ClampRecentToolsVisibleRows(value));
    }

    private bool _showHomeClipboardHistory = true;
    public bool ShowHomeClipboardHistory
    {
        get => _showHomeClipboardHistory;
        set => SetField(ref _showHomeClipboardHistory, value);
    }

    private int _quickLaunchVisibleRows = 2;
    public int QuickLaunchVisibleRows
    {
        get => _quickLaunchVisibleRows;
        set => SetField(ref _quickLaunchVisibleRows, ClampQuickLaunchVisibleRows(value));
    }

    private bool _aiEnabled;
    public bool AiEnabled
    {
        get => _aiEnabled;
        set => SetField(ref _aiEnabled, value);
    }

    private string _aiBaseUrl = "https://api.openai.com/v1";
    public string AiBaseUrl
    {
        get => _aiBaseUrl;
        set => SetField(ref _aiBaseUrl, NormalizeBaseUrl(value));
    }

    private string _aiModel = "gpt-4.1-mini";
    public string AiModel
    {
        get => _aiModel;
        set => SetField(ref _aiModel, NormalizeModel(value));
    }

    private string _aiApiKeyEncrypted = string.Empty;
    public string AiApiKeyEncrypted
    {
        get => _aiApiKeyEncrypted;
        set => SetField(ref _aiApiKeyEncrypted, value?.Trim() ?? string.Empty);
    }

    private string _aiApiKeyPreview = string.Empty;
    public string AiApiKeyPreview
    {
        get => _aiApiKeyPreview;
        set => SetField(ref _aiApiKeyPreview, value?.Trim() ?? string.Empty);
    }

    private double _aiTemperature = 0.2;
    public double AiTemperature
    {
        get => _aiTemperature;
        set => SetField(ref _aiTemperature, ClampTemperature(value));
    }

    private AgentConversationMode _aiDefaultMode = AgentConversationMode.Chat;
    public AgentConversationMode AiDefaultMode
    {
        get => _aiDefaultMode;
        set => SetField(ref _aiDefaultMode, value);
    }

    private AgentActionMode _aiActionMode = AgentActionMode.ConfirmBeforeAction;
    public AgentActionMode AiActionMode
    {
        get => _aiActionMode;
        set => SetField(ref _aiActionMode, value);
    }

    private bool _aiAllowClipboardTools;
    public bool AiAllowClipboardTools
    {
        get => _aiAllowClipboardTools;
        set => SetField(ref _aiAllowClipboardTools, value);
    }

    public void Load()
    {
        var values = _store.LoadAll();

        _drawerWidth = GetInt(values, nameof(DrawerWidth), _drawerWidth);
        _triggerSides = NormalizeTriggerSides(GetEnum(values, nameof(TriggerSides), _triggerSides));
        _edgeHandleWidth = ClampEdgeHandleWidth(GetInt(values, nameof(EdgeHandleWidth), _edgeHandleWidth));
        _edgeHandleHeight = ClampEdgeHandleHeight(GetInt(values, nameof(EdgeHandleHeight), _edgeHandleHeight));
        _autoHideDelayMs = GetInt(values, nameof(AutoHideDelayMs), _autoHideDelayMs);
        _animationDurationMs = GetInt(values, nameof(AnimationDurationMs), _animationDurationMs);
        _disableInFullscreen = GetBool(values, nameof(DisableInFullscreen), _disableInFullscreen);
        _menuExpanded = GetBool(values, nameof(MenuExpanded), _menuExpanded);
        _globalHotkeyText = NormalizeHotkeyText(GetString(values, nameof(GlobalHotkeyText), _globalHotkeyText));
        _useEverythingSearch = GetBool(values, nameof(UseEverythingSearch), _useEverythingSearch);
        _trackRecentWindows = GetBool(values, nameof(TrackRecentWindows), _trackRecentWindows);
        _trackRecentTools = GetBool(values, nameof(TrackRecentTools), _trackRecentTools);
        _recentWindowsDisplayLimit = ClampRecentDisplayLimit(
            GetInt(values, nameof(RecentWindowsDisplayLimit), _recentWindowsDisplayLimit));
        _recentToolsVisibleRows = ClampRecentToolsVisibleRows(
            GetInt(values, nameof(RecentToolsVisibleRows), _recentToolsVisibleRows));
        _showHomeClipboardHistory = GetBool(
            values,
            nameof(ShowHomeClipboardHistory),
            _showHomeClipboardHistory);
        _quickLaunchVisibleRows = ClampQuickLaunchVisibleRows(
            GetInt(values, nameof(QuickLaunchVisibleRows), _quickLaunchVisibleRows));
        _aiEnabled = GetBool(values, nameof(AiEnabled), _aiEnabled);
        _aiBaseUrl = NormalizeBaseUrl(GetString(values, nameof(AiBaseUrl), _aiBaseUrl));
        _aiModel = NormalizeModel(GetString(values, nameof(AiModel), _aiModel));
        _aiApiKeyEncrypted = GetString(values, nameof(AiApiKeyEncrypted), _aiApiKeyEncrypted);
        _aiApiKeyPreview = GetString(values, nameof(AiApiKeyPreview), _aiApiKeyPreview);
        _aiTemperature = ClampTemperature(GetDouble(values, nameof(AiTemperature), _aiTemperature));
        _aiDefaultMode = GetEnum(values, nameof(AiDefaultMode), _aiDefaultMode);
        _aiActionMode = GetEnum(values, nameof(AiActionMode), _aiActionMode);
        _aiAllowClipboardTools = GetBool(values, nameof(AiAllowClipboardTools), _aiAllowClipboardTools);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        _store.Set(nameof(DrawerWidth), _drawerWidth.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(TriggerSides), _triggerSides.ToString());
        _store.Set(nameof(EdgeHandleWidth), _edgeHandleWidth.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(EdgeHandleHeight), _edgeHandleHeight.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(AutoHideDelayMs), _autoHideDelayMs.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(AnimationDurationMs), _animationDurationMs.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(DisableInFullscreen), _disableInFullscreen ? "1" : "0");
        _store.Set(nameof(MenuExpanded), _menuExpanded ? "1" : "0");
        _store.Set(nameof(GlobalHotkeyText), _globalHotkeyText);
        _store.Set(nameof(UseEverythingSearch), _useEverythingSearch ? "1" : "0");
        _store.Set(nameof(TrackRecentWindows), _trackRecentWindows ? "1" : "0");
        _store.Set(nameof(TrackRecentTools), _trackRecentTools ? "1" : "0");
        _store.Set(nameof(RecentWindowsDisplayLimit), _recentWindowsDisplayLimit.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(RecentToolsVisibleRows), _recentToolsVisibleRows.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(ShowHomeClipboardHistory), _showHomeClipboardHistory ? "1" : "0");
        _store.Set(nameof(QuickLaunchVisibleRows), _quickLaunchVisibleRows.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(AiEnabled), _aiEnabled ? "1" : "0");
        _store.Set(nameof(AiBaseUrl), _aiBaseUrl);
        _store.Set(nameof(AiModel), _aiModel);
        _store.Set(nameof(AiApiKeyEncrypted), _aiApiKeyEncrypted);
        _store.Set(nameof(AiApiKeyPreview), _aiApiKeyPreview);
        _store.Set(nameof(AiTemperature), _aiTemperature.ToString(CultureInfo.InvariantCulture));
        _store.Set(nameof(AiDefaultMode), _aiDefaultMode.ToString());
        _store.Set(nameof(AiActionMode), _aiActionMode.ToString());
        _store.Set(nameof(AiAllowClipboardTools), _aiAllowClipboardTools ? "1" : "0");
        _store.Save();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetField<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
        => values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var raw) ? raw == "1" : fallback;

    private static string GetString(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw
            : fallback;

    private static TEnum GetEnum<TEnum>(IReadOnlyDictionary<string, string> values, string key, TEnum fallback)
        where TEnum : struct, Enum
        => values.TryGetValue(key, out var raw)
            && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static string NormalizeHotkeyText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Ctrl+Alt+K" : value.Trim();

    private static string NormalizeBaseUrl(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "https://api.openai.com/v1"
            : value.Trim();
        return trimmed.TrimEnd('/');
    }

    private static string NormalizeModel(string? value)
        => string.IsNullOrWhiteSpace(value) ? "gpt-4.1-mini" : value.Trim();

    private static EdgeTriggerSides NormalizeTriggerSides(EdgeTriggerSides value)
    {
        var masked = value & EdgeTriggerSides.Both;
        return masked == 0 ? EdgeTriggerSides.Left : masked;
    }

    private static int ClampEdgeHandleWidth(int value) => Math.Clamp(value, 24, 80);

    private static int ClampEdgeHandleHeight(int value) => Math.Max(120, value);

    private static int ClampRecentToolsVisibleRows(int value) => Math.Clamp(value, 1, 4);

    private static int ClampRecentDisplayLimit(int value) => Math.Clamp(value, 1, 20);

    private static int ClampQuickLaunchVisibleRows(int value) => Math.Clamp(value, 1, 4);

    private static double ClampTemperature(double value)
        => double.IsNaN(value) ? 0.2 : Math.Clamp(value, 0, 2);
}
