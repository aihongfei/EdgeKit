namespace EdgeKit.Core.Agent;

public interface IAgentRepository
{
    event EventHandler? Changed;

    IReadOnlyList<AgentConversation> GetConversations(bool includeArchived = false);

    AgentConversationDetail? GetConversation(long id);

    AgentConversation CreateConversation(string title, AgentConversationMode mode, string model);

    void UpdateConversation(long id, string title, AgentConversationMode mode, string model);

    void TouchConversation(long id);

    void ArchiveConversation(long id, bool archived);

    void DeleteConversation(long id);

    void SaveSession(long id, string sessionJson);

    AgentContextSummary? GetContextSummary(long conversationId);

    AgentContextSummary SaveContextSummary(
        long conversationId,
        string summary,
        int sourceMessageSequence,
        int sourceToolCallId,
        int estimatedTokens);

    void DeleteContextSummary(long conversationId);

    AgentMessage AddMessage(
        long conversationId,
        AgentMessageRole role,
        string content,
        AgentMessageStatus status = AgentMessageStatus.Complete,
        string error = "");

    void UpdateMessage(long messageId, string content, AgentMessageStatus status, string error = "", string activityText = "");

    void UpdateMessageActivity(long messageId, AgentMessageStatus status, string activityText, string error = "");

    AgentToolCall AddToolCall(
        long conversationId,
        long? messageId,
        string toolId,
        string toolName,
        AgentToolRisk risk,
        string argumentsJson,
        string argumentsSummary,
        AgentToolApprovalStatus approvalStatus,
        AgentToolExecutionStatus executionStatus);

    AgentToolCall? GetToolCall(long id);

    IReadOnlyList<AgentToolCall> GetPendingToolCalls(long conversationId);

    AgentToolCall UpdateToolCall(
        long id,
        AgentToolApprovalStatus approvalStatus,
        AgentToolExecutionStatus executionStatus,
        string resultSummary,
        string error = "");
}
