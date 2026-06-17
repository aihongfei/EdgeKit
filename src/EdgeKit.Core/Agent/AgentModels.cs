namespace EdgeKit.Core.Agent;

public enum AgentConversationMode
{
    Chat,
    Translate,
    WindowsConfig
}

public enum AgentActionMode
{
    SuggestOnly,
    ConfirmBeforeAction,
    AutoWithWhitelist
}

public enum AgentSearchProvider
{
    Brave,
    Tavily
}

public enum AgentMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public enum AgentMessageStatus
{
    Complete,
    Pending,
    Failed
}

public enum AgentToolRisk
{
    ReadOnly,
    OpensExternal,
    UserWrite,
    SystemWrite,
    Destructive
}

public enum AgentToolApprovalStatus
{
    NotRequired,
    Pending,
    Approved,
    Rejected
}

public enum AgentToolExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public sealed record AgentSettings(
    bool Enabled,
    string BaseUrl,
    string Model,
    string ApiKey,
    string ApiKeyPreview,
    double Temperature,
    AgentConversationMode DefaultMode,
    AgentActionMode ActionMode,
    bool AllowClipboardTools,
    bool EnableFileTools,
    bool EnableShellTools,
    bool EnableWebTools,
    bool EnableMcpTools,
    AgentSearchProvider SearchProvider,
    string SearchApiKey,
    string SearchApiKeyPreview,
    string TrustedDirectories,
    string ShellCommandWhitelist,
    string McpServersJson,
    int ContextWindowTokens = 256000);

public sealed record AgentConversation(
    long Id,
    string Title,
    AgentConversationMode Mode,
    string Model,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    bool IsArchived,
    string AgentSessionJson);

public sealed record AgentMessage(
    long Id,
    long ConversationId,
    AgentMessageRole Role,
    string Content,
    int Sequence,
    AgentMessageStatus Status,
    string ActivityText,
    string Error,
    DateTime CreatedUtc);

public sealed record AgentToolCall(
    long Id,
    long ConversationId,
    long? MessageId,
    string ToolId,
    string ToolName,
    AgentToolRisk Risk,
    string ArgumentsJson,
    string ArgumentsSummary,
    string ResultSummary,
    AgentToolApprovalStatus ApprovalStatus,
    AgentToolExecutionStatus ExecutionStatus,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    string Error);

public sealed record AgentContextSummary(
    long Id,
    long ConversationId,
    string Summary,
    int SourceMessageSequence,
    int SourceToolCallId,
    int EstimatedTokens,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record AgentContextStatus(
    long ConversationId,
    int EstimatedTokens,
    int ContextWindowTokens,
    double UsageRatio,
    int MessageCount,
    int ToolSummaryCount,
    int CompressionSummaryCount,
    bool IsCompressing,
    DateTime? LastCompressedUtc,
    string Preview,
    int SystemTokens = 0,
    int ToolsTokens = 0,
    int ConversationTokens = 0,
    int InstructionsTokens = 0,
    int ToolDefinitionsTokens = 0);

public sealed record AgentConversationDetail(
    AgentConversation Conversation,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolCall> ToolCalls,
    IReadOnlyList<AgentContextSummary> ContextSummaries);

public sealed record AgentSendResult(
    bool Success,
    AgentMessage? UserMessage,
    AgentMessage? AssistantMessage,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string ErrorMessage);

public enum AgentStreamEventKind
{
    Started,
    ContextChanged,
    Delta,
    ToolCallsChanged,
    PausedForToolApproval,
    Completed,
    Failed
}

public sealed record AgentStreamEvent(
    AgentStreamEventKind Kind,
    AgentMessage? UserMessage,
    AgentMessage? AssistantMessage,
    string Delta,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string ErrorMessage,
    AgentContextStatus? ContextStatus = null);

public sealed record AgentToolApprovalResult(
    bool Success,
    AgentToolCall? ToolCall,
    AgentMessage? ToolMessage,
    AgentMessage? AssistantMessage,
    string Message);

public sealed record AgentConnectionTestResult(bool Success, string Message);
