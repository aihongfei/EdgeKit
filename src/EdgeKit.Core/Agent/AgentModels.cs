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
    bool AllowClipboardTools);

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

public sealed record AgentConversationDetail(
    AgentConversation Conversation,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolCall> ToolCalls);

public sealed record AgentSendResult(
    bool Success,
    AgentMessage? UserMessage,
    AgentMessage? AssistantMessage,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string ErrorMessage);

public sealed record AgentToolApprovalResult(
    bool Success,
    AgentToolCall? ToolCall,
    AgentMessage? ToolMessage,
    AgentMessage? AssistantMessage,
    string Message);

public sealed record AgentConnectionTestResult(bool Success, string Message);
