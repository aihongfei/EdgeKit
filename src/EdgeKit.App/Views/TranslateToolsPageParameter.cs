using EdgeKit.Core.Agent;
using EdgeKit.Services.Text;

namespace EdgeKit.App.Views;

/// <summary>翻译工具页导航参数。</summary>
public sealed record TranslateToolsPageParameter(
    IAgentService AgentService,
    YoudaoTranslationService Youdao,
    nint Hwnd);