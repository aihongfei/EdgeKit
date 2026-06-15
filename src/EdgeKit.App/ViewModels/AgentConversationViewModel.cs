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

    public string Detail => Conversation.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
}
