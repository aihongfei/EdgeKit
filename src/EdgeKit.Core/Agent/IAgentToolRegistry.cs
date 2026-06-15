namespace EdgeKit.Core.Agent;

public interface IAgentToolRegistry
{
    IReadOnlyList<AgentToolDescriptor> GetTools(AgentConversationMode mode, AgentSettings settings);

    AgentToolDescriptor? Find(string toolId);
}

public sealed record AgentToolDescriptor(
    string Id,
    string Name,
    string Description,
    AgentToolRisk Risk,
    AgentConversationMode[] Modes,
    bool RequiresApproval);
