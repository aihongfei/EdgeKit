using EdgeKit.Core.Agent;
using Microsoft.UI.Xaml;

namespace EdgeKit.App.ViewModels;

public sealed class AgentConversationViewModel
{
    public AgentConversationViewModel(AgentConversation conversation, bool isTitleGenerating = false)
    {
        Conversation = conversation;
        IsTitleGenerating = isTitleGenerating;
    }

    public AgentConversation Conversation { get; }

    public bool IsTitleGenerating { get; }

    public long Id => Conversation.Id;

    public string Title => Conversation.Title;

    public string Detail => IsTitleGenerating
        ? "正在生成标题..."
        : Conversation.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public Visibility TitleLoadingVisibility => IsTitleGenerating ? Visibility.Visible : Visibility.Collapsed;
}
