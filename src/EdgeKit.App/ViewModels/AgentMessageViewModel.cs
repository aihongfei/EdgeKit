using EdgeKit.Core.Agent;

namespace EdgeKit.App.ViewModels;

public sealed class AgentMessageViewModel
{
    public AgentMessageViewModel(AgentMessage message)
    {
        Message = message;
    }

    public AgentMessage Message { get; }

    public string RoleText => Message.Role switch
    {
        AgentMessageRole.User => "你",
        AgentMessageRole.Assistant => "智能体",
        AgentMessageRole.Tool => "工具",
        AgentMessageRole.System => "系统",
        _ => Message.Role.ToString()
    };

    public string Content => Message.Status == AgentMessageStatus.Failed
        ? "失败: " + Message.Error
        : Message.Content;

    public string TimeText => Message.CreatedUtc.ToLocalTime().ToString("HH:mm");
}
