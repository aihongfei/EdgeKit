using EdgeKit.Core.Agent;

namespace EdgeKit.App.ViewModels;

public sealed class AgentConversationViewModel
{
    public AgentConversationViewModel(AgentConversation conversation)
    {
        Conversation = conversation;
    }

    public AgentConversation Conversation { get; }

    public long Id => Conversation.Id;

    public string Title => Conversation.Title;

    public string Detail => $"{ModeText} · {Conversation.UpdatedUtc.ToLocalTime():MM-dd HH:mm}";

    public string ModeText => Conversation.Mode switch
    {
        AgentConversationMode.Translate => "翻译",
        AgentConversationMode.WindowsConfig => "Windows 配置",
        _ => "对话"
    };
}
