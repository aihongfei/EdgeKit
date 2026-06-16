namespace EdgeKit.Core.Agent;

public interface IAgentService
{
    event EventHandler? Changed;

    AgentSettings GetSettings();

    void SaveSettings(AgentSettings settings);

    Task<AgentConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<AgentConversation> GetConversations(bool includeArchived = false);

    AgentConversationDetail? GetConversation(long id);

    AgentContextStatus? GetContextStatus(long conversationId);

    AgentConversation CreateConversation(AgentConversationMode mode);

    void UpdateConversation(long id, string title, AgentConversationMode mode);

    void ArchiveConversation(long id, bool archived);

    void DeleteConversation(long id);

    Task TryGenerateConversationTitleAsync(long id, string firstUserMessage, CancellationToken cancellationToken = default);

    Task<AgentSendResult> SendAsync(long conversationId, string message, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentStreamEvent> SendStreamingAsync(long conversationId, string message, CancellationToken cancellationToken = default);

    Task<AgentToolApprovalResult> ApproveToolCallAsync(long toolCallId, CancellationToken cancellationToken = default);

    AgentToolApprovalResult RejectToolCall(long toolCallId);
}
