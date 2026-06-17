using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EdgeKit.Core.Agent;
using EdgeKit.Core.Services;
using EdgeKit.Services.Settings;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace EdgeKit.Services.Agent;

public sealed class AgentService : IAgentService
{
    private const int MaxToolCallsPerTurn = 8;
    private const double ContextCompressionThreshold = 0.8;
    private const int ContextRecentLedgerItemsToKeep = 20;
    private const int ToolContextSummaryMaxChars = 2000;
    private const int MessageContextMaxChars = 12000;
    private const string WaitingForToolApprovalText = "等待确认工具调用...";
    private const string StoppedByUserActivityText = "已中止本次回复。";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISettingsService _settings;
    private readonly IAgentRepository _repository;
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly McpToolService _mcpTools;
    private readonly SemaphoreSlim _agentRunLock = new(1, 1);

    private long _activeConversationId;
    private long _activeAssistantMessageId;
    private int _toolCallsThisTurn;
    private AgentToolCall? _pendingApprovalCall;
    private AgentStreamContext? _activeStreamContext;
    private readonly Dictionary<string, long> _toolCallsByTurnSignature = new(StringComparer.Ordinal);
    private readonly HashSet<long> _compressingConversationIds = new();

    public AgentService(
        ISettingsService settings,
        IAgentRepository repository,
        AgentToolRegistry toolRegistry,
        AgentToolExecutor toolExecutor,
        McpToolService mcpTools)
    {
        _settings = settings;
        _repository = repository;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _mcpTools = mcpTools;

        _repository.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public AgentSettings GetSettings()
    {
        var apiKey = string.Empty;
        var searchApiKey = string.Empty;
        try
        {
            apiKey = SecretProtector.Unprotect(_settings.AiApiKeyEncrypted);
        }
        catch
        {
            apiKey = string.Empty;
        }

        try
        {
            searchApiKey = SecretProtector.Unprotect(_settings.AiSearchApiKeyEncrypted);
        }
        catch
        {
            searchApiKey = string.Empty;
        }

        return new AgentSettings(
            _settings.AiEnabled,
            _settings.AiBaseUrl,
            _settings.AiModel,
            apiKey,
            _settings.AiApiKeyPreview,
            _settings.AiTemperature,
            _settings.AiDefaultMode,
            _settings.AiActionMode,
            _settings.AiAllowClipboardTools,
            _settings.AiEnableFileTools,
            _settings.AiEnableShellTools,
            _settings.AiEnableWebTools,
            _settings.AiEnableMcpTools,
            _settings.AiSearchProvider,
            searchApiKey,
            _settings.AiSearchApiKeyPreview,
            _settings.AiTrustedDirectories,
            _settings.AiShellCommandWhitelist,
            _settings.AiMcpServersJson,
            _settings.AiContextWindowTokens);
    }

    public void SaveSettings(AgentSettings settings)
    {
        _settings.AiEnabled = settings.Enabled;
        _settings.AiBaseUrl = settings.BaseUrl;
        _settings.AiModel = settings.Model;
        _settings.AiTemperature = settings.Temperature;
        _settings.AiDefaultMode = AgentConversationMode.Chat;
        _settings.AiActionMode = settings.ActionMode;
        _settings.AiAllowClipboardTools = settings.AllowClipboardTools;

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _settings.AiApiKeyEncrypted = SecretProtector.Protect(settings.ApiKey.Trim());
            _settings.AiApiKeyPreview = SecretProtector.BuildPreview(settings.ApiKey.Trim());
        }

        _settings.AiEnableFileTools = settings.EnableFileTools;
        _settings.AiEnableShellTools = settings.EnableShellTools;
        _settings.AiEnableWebTools = settings.EnableWebTools;
        _settings.AiEnableMcpTools = settings.EnableMcpTools;
        _settings.AiSearchProvider = settings.SearchProvider;
        _settings.AiTrustedDirectories = settings.TrustedDirectories;
        _settings.AiShellCommandWhitelist = settings.ShellCommandWhitelist;
        _settings.AiMcpServersJson = settings.McpServersJson;
        _settings.AiContextWindowTokens = settings.ContextWindowTokens;

        if (!string.IsNullOrWhiteSpace(settings.SearchApiKey))
        {
            _settings.AiSearchApiKeyEncrypted = SecretProtector.Protect(settings.SearchApiKey.Trim());
            _settings.AiSearchApiKeyPreview = SecretProtector.BuildPreview(settings.SearchApiKey.Trim());
        }

        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AgentConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        var validation = ValidateSettings(settings);
        if (validation is not null)
        {
            return new AgentConnectionTestResult(false, validation);
        }

        try
        {
            var agent = await BuildAgentAsync(settings, AgentConversationMode.Chat, cancellationToken).ConfigureAwait(false);
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var response = await agent.RunAsync(
                "请只回复“OK”。",
                session,
                BuildRunOptions(settings, AgentConversationMode.Chat),
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.Text)
                ? new AgentConnectionTestResult(false, "模型没有返回内容")
                : new AgentConnectionTestResult(true, "连接成功: " + response.Text.Trim());
        }
        catch (Exception ex) when (IsAgentCallException(ex))
        {
            return new AgentConnectionTestResult(false, "连接失败: " + ex.Message);
        }
    }

    public IReadOnlyList<AgentConversation> GetConversations(bool includeArchived = false)
        => _repository.GetConversations(includeArchived);

    public AgentConversationDetail? GetConversation(long id)
        => _repository.GetConversation(id);

    public AgentContextStatus? GetContextStatus(long conversationId)
    {
        var detail = _repository.GetConversation(conversationId);
        if (detail is null)
        {
            return null;
        }

        return BuildContextPackage(detail, GetSettings(), null).Status;
    }

    public AgentConversation CreateConversation(AgentConversationMode mode)
    {
        var settings = GetSettings();
        return _repository.CreateConversation("新对话", AgentConversationMode.Chat, settings.Model);
    }

    public void UpdateConversation(long id, string title, AgentConversationMode mode)
    {
        var settings = GetSettings();
        _repository.UpdateConversation(id, string.IsNullOrWhiteSpace(title) ? "未命名会话" : title.Trim(), AgentConversationMode.Chat, settings.Model);
    }

    public void ArchiveConversation(long id, bool archived)
        => _repository.ArchiveConversation(id, archived);

    public void DeleteConversation(long id)
        => _repository.DeleteConversation(id);

    public async Task TryGenerateConversationTitleAsync(long id, string firstUserMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return;
        }

        var detail = _repository.GetConversation(id);
        if (detail is null || !IsDefaultConversationTitle(detail.Conversation.Title))
        {
            return;
        }

        var title = await GenerateConversationTitleAsync(firstUserMessage, cancellationToken).ConfigureAwait(false);
        title = NormalizeConversationTitle(title);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = BuildFallbackConversationTitle(firstUserMessage);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            _repository.UpdateConversation(id, title, AgentConversationMode.Chat, GetSettings().Model);
        }
    }

    public async Task<AgentSendResult> SendAsync(long conversationId, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new AgentSendResult(false, null, null, Array.Empty<AgentToolCall>(), "请输入消息");
        }

        var settings = GetSettings();
        var validation = ValidateSettings(settings);
        if (validation is not null)
        {
            return new AgentSendResult(false, null, null, Array.Empty<AgentToolCall>(), validation);
        }

        var detail = _repository.GetConversation(conversationId);
        if (detail is null)
        {
            return new AgentSendResult(false, null, null, Array.Empty<AgentToolCall>(), "会话不存在");
        }

        var userMessage = _repository.AddMessage(conversationId, AgentMessageRole.User, message.Trim());
        await _agentRunLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _activeConversationId = conversationId;
        var assistantMessage = _repository.AddMessage(conversationId, AgentMessageRole.Assistant, string.Empty, AgentMessageStatus.Pending);
        _activeAssistantMessageId = assistantMessage.Id;
        _activeStreamContext = null;
        _toolCallsThisTurn = 0;
        _pendingApprovalCall = null;
        _toolCallsByTurnSignature.Clear();

        try
        {
            detail = _repository.GetConversation(conversationId) ?? detail;
            var agent = await BuildAgentAsync(settings, AgentConversationMode.Chat, cancellationToken).ConfigureAwait(false);
            var preparedContext = await PrepareContextAsync(
                detail,
                settings,
                cancellationToken,
                excludeMessageId: userMessage.Id).ConfigureAwait(false);
            var session = await CreateSessionAsync(agent, preparedContext, cancellationToken).ConfigureAwait(false);
            var runMessage = BuildContinuationPromptIfNeeded(preparedContext.Detail, message.Trim(), userMessage.Id) ?? message.Trim();
            var response = await agent.RunAsync(
                runMessage,
                session,
                BuildRunOptions(settings, AgentConversationMode.Chat),
                cancellationToken).ConfigureAwait(false);
            if (_pendingApprovalCall is not null)
            {
                assistantMessage = MarkAssistantWaitingForToolApproval(assistantMessage, response.Text);
                return new AgentSendResult(true, userMessage, assistantMessage, GetToolCalls(conversationId), string.Empty);
            }

            var assistantText = string.IsNullOrWhiteSpace(response.Text)
                ? "(模型没有返回文本内容)"
                : response.Text.Trim();
            _repository.UpdateMessage(assistantMessage.Id, assistantText, AgentMessageStatus.Complete);
            assistantMessage = assistantMessage with
            {
                Content = assistantText,
                Status = AgentMessageStatus.Complete,
                ActivityText = string.Empty,
                Error = string.Empty
            };
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);

            return new AgentSendResult(true, userMessage, assistantMessage, GetToolCalls(conversationId), string.Empty);
        }
        catch (Exception ex) when (TryGetApprovalRequiredException(ex, out _))
        {
            var waitingAssistant = MarkAssistantWaitingForToolApproval(assistantMessage);
            return new AgentSendResult(true, userMessage, waitingAssistant, GetToolCalls(conversationId), string.Empty);
        }
        catch (Exception ex) when (IsAgentCallException(ex))
        {
            _repository.UpdateMessage(assistantMessage.Id, string.Empty, AgentMessageStatus.Failed, ex.Message);
            var failed = assistantMessage with
            {
                Content = string.Empty,
                Status = AgentMessageStatus.Failed,
                ActivityText = string.Empty,
                Error = ex.Message
            };
            return new AgentSendResult(false, userMessage, failed, GetToolCalls(conversationId), ex.Message);
        }
        finally
        {
            _activeConversationId = 0;
            _activeAssistantMessageId = 0;
            _activeStreamContext = null;
            _toolCallsThisTurn = 0;
            _pendingApprovalCall = null;
            _toolCallsByTurnSignature.Clear();
            _agentRunLock.Release();
        }
    }

    public async IAsyncEnumerable<AgentStreamEvent> SendStreamingAsync(
        long conversationId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(
            () => ProduceStreamingResponseAsync(conversationId, message, channel.Writer, cancellationToken),
            CancellationToken.None);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async Task ProduceStreamingResponseAsync(
        long conversationId,
        string message,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        AgentMessage? userMessage = null;
        AgentMessage? assistantMessage = null;
        var responseText = new StringBuilder();
        var lastToolSignature = string.Empty;
        var lockHeld = false;

        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                await writer.WriteAsync(FailedStreamEvent(null, null, conversationId, "请输入消息"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var settings = GetSettings();
            var validation = ValidateSettings(settings);
            if (validation is not null)
            {
                await writer.WriteAsync(FailedStreamEvent(null, null, conversationId, validation), cancellationToken).ConfigureAwait(false);
                return;
            }

            var detail = _repository.GetConversation(conversationId);
            if (detail is null)
            {
                await writer.WriteAsync(FailedStreamEvent(null, null, conversationId, "会话不存在"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var trimmedMessage = message.Trim();
            userMessage = _repository.AddMessage(conversationId, AgentMessageRole.User, trimmedMessage);
            assistantMessage = _repository.AddMessage(conversationId, AgentMessageRole.Assistant, string.Empty, AgentMessageStatus.Pending);
            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.Started, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty),
                cancellationToken).ConfigureAwait(false);

            await _agentRunLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockHeld = true;
            _activeConversationId = conversationId;
            _activeAssistantMessageId = assistantMessage.Id;
            _activeStreamContext = new AgentStreamContext(writer, userMessage, assistantMessage, CancellationToken.None);
            _toolCallsThisTurn = 0;
            _pendingApprovalCall = null;
            _toolCallsByTurnSignature.Clear();

            var agent = await BuildAgentAsync(settings, AgentConversationMode.Chat, cancellationToken).ConfigureAwait(false);
            detail = _repository.GetConversation(conversationId) ?? detail;
            var contextStatus = BuildContextPackage(detail, settings, null, userMessage.Id).Status;
            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.ContextChanged, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty, contextStatus),
                cancellationToken).ConfigureAwait(false);

            var preparedContext = await PrepareContextAsync(
                detail,
                settings,
                cancellationToken,
                writer,
                userMessage,
                assistantMessage,
                excludeMessageId: userMessage.Id).ConfigureAwait(false);
            var session = await CreateSessionAsync(agent, preparedContext, cancellationToken).ConfigureAwait(false);
            var runMessage = BuildContinuationPromptIfNeeded(preparedContext.Detail, trimmedMessage, userMessage.Id) ?? trimmedMessage;
            var charsSinceContextUpdate = 0;
            await foreach (var update in agent.RunStreamingAsync(
                    runMessage,
                    session,
                    BuildRunOptions(settings, AgentConversationMode.Chat),
                    cancellationToken).ConfigureAwait(false))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    responseText.Append(delta);
                    charsSinceContextUpdate += delta.Length;
                    await writer.WriteAsync(
                        new AgentStreamEvent(AgentStreamEventKind.Delta, userMessage, assistantMessage, delta, GetToolCalls(conversationId), string.Empty),
                        cancellationToken).ConfigureAwait(false);

                    if (charsSinceContextUpdate >= 512)
                    {
                        charsSinceContextUpdate = 0;
                        detail = _repository.GetConversation(conversationId) ?? detail;
                        var streamingContextStatus = BuildContextPackage(detail, settings, null, userMessage.Id).Status;
                        await writer.WriteAsync(
                            new AgentStreamEvent(AgentStreamEventKind.ContextChanged, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty, streamingContextStatus),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                lastToolSignature = await WriteToolChangesIfNeededAsync(
                    writer,
                    conversationId,
                    userMessage,
                    assistantMessage,
                    lastToolSignature,
                    cancellationToken).ConfigureAwait(false);
            }

            if (_pendingApprovalCall is not null)
            {
                assistantMessage = MarkAssistantWaitingForToolApproval(assistantMessage, responseText.ToString());
                await writer.WriteAsync(
                    new AgentStreamEvent(AgentStreamEventKind.PausedForToolApproval, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty),
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var assistantText = responseText.Length == 0
                ? "(模型没有返回文本内容)"
                : responseText.ToString().Trim();
            _repository.UpdateMessage(assistantMessage.Id, assistantText, AgentMessageStatus.Complete);
            var completedAssistant = assistantMessage with
            {
                Content = assistantText,
                Status = AgentMessageStatus.Complete,
                ActivityText = string.Empty,
                Error = string.Empty
            };
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);

            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.Completed, userMessage, completedAssistant, string.Empty, GetToolCalls(conversationId), string.Empty),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TryGetApprovalRequiredException(ex, out _))
        {
            if (assistantMessage is not null)
            {
                assistantMessage = MarkAssistantWaitingForToolApproval(assistantMessage, responseText.ToString());
            }

            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.PausedForToolApproval, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (assistantMessage is not null)
            {
                assistantMessage = MarkAssistantStoppedAfterCancellation(assistantMessage, responseText.ToString());
                SkipPendingToolsForAssistant(conversationId, assistantMessage.Id, "已中止本次回复");
            }

            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.Completed, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAgentCallException(ex) || ex is OperationCanceledException)
        {
            if (assistantMessage is not null)
            {
                _repository.UpdateMessage(assistantMessage.Id, string.Empty, AgentMessageStatus.Failed, ex.Message);
                assistantMessage = assistantMessage with
                {
                    Content = string.Empty,
                    Status = AgentMessageStatus.Failed,
                    ActivityText = string.Empty,
                    Error = ex.Message
                };
            }

            await writer.WriteAsync(FailedStreamEvent(userMessage, assistantMessage, conversationId, ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (assistantMessage is not null)
            {
                _repository.UpdateMessage(assistantMessage.Id, string.Empty, AgentMessageStatus.Failed, ex.Message);
                assistantMessage = assistantMessage with
                {
                    Content = string.Empty,
                    Status = AgentMessageStatus.Failed,
                    ActivityText = string.Empty,
                    Error = ex.Message
                };
            }

            await writer.WriteAsync(FailedStreamEvent(userMessage, assistantMessage, conversationId, ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _activeConversationId = 0;
            _activeAssistantMessageId = 0;
            _activeStreamContext = null;
            _toolCallsThisTurn = 0;
            _pendingApprovalCall = null;
            _toolCallsByTurnSignature.Clear();
            if (lockHeld)
            {
                _agentRunLock.Release();
            }

            writer.TryComplete();
        }
    }

    public async Task<AgentToolApprovalResult> ApproveToolCallAsync(long toolCallId, CancellationToken cancellationToken = default)
    {
        var call = _repository.GetToolCall(toolCallId);
        if (call is null)
        {
            return new AgentToolApprovalResult(false, null, null, null, "工具调用不存在");
        }

        if (call.ApprovalStatus != AgentToolApprovalStatus.Pending)
        {
            return new AgentToolApprovalResult(false, call, null, null, "工具调用不是待审批状态");
        }

        var args = AgentToolExecutor.ParseArguments(call.ArgumentsJson);
        _repository.UpdateToolCall(call.Id, AgentToolApprovalStatus.Approved, AgentToolExecutionStatus.Running, string.Empty);
        var result = await _toolExecutor.ExecuteAsync(call.ToolId, args, cancellationToken).ConfigureAwait(false);
        var updated = _repository.UpdateToolCall(
            call.Id,
            AgentToolApprovalStatus.Approved,
            result.Success ? AgentToolExecutionStatus.Succeeded : AgentToolExecutionStatus.Failed,
            result.Success ? result.Result : string.Empty,
            result.Error);

        var toolMessage = _repository.AddMessage(
            call.ConversationId,
            AgentMessageRole.Tool,
            result.Success ? result.Result : "工具执行失败: " + result.Error,
            result.Success ? AgentMessageStatus.Complete : AgentMessageStatus.Failed,
            result.Error);

        AgentMessage? assistantMessage = null;
        if (result.Success)
        {
            assistantMessage = MarkAssistantWaitingForToolFollowUp(updated);
            if (assistantMessage is not null)
            {
                ScheduleToolFollowUp(updated, assistantMessage.Id);
            }
        }
        else
        {
            assistantMessage = MarkAssistantStoppedAfterToolFailed(updated);
        }

        return new AgentToolApprovalResult(result.Success, updated, toolMessage, assistantMessage, result.Success ? "工具已执行" : result.Error);
    }

    public AgentToolApprovalResult RejectToolCall(long toolCallId)
    {
        var call = _repository.GetToolCall(toolCallId);
        if (call is null)
        {
            return new AgentToolApprovalResult(false, null, null, null, "工具调用不存在");
        }

        var updated = _repository.UpdateToolCall(
            call.Id,
            AgentToolApprovalStatus.Rejected,
            AgentToolExecutionStatus.Skipped,
            "用户拒绝执行",
            string.Empty);
        var message = _repository.AddMessage(call.ConversationId, AgentMessageRole.Tool, "用户拒绝执行工具: " + call.ToolName);
        var assistant = MarkAssistantStoppedAfterToolRejected(updated);
        return new AgentToolApprovalResult(true, updated, message, assistant, "已拒绝");
    }

    private AgentMessage? MarkAssistantWaitingForToolFollowUp(AgentToolCall call)
    {
        var detail = _repository.GetConversation(call.ConversationId);
        if (detail is null)
        {
            return null;
        }

        var assistant = ResolveFollowUpAssistantMessage(detail, call);
        if (assistant is null)
        {
            return null;
        }

        var activity = $"工具 {call.ToolName} 已执行完成，正在继续处理...";
        _repository.UpdateMessageActivity(assistant.Id, AgentMessageStatus.Pending, activity);
        return assistant with
        {
            Status = AgentMessageStatus.Pending,
            ActivityText = activity,
            Error = string.Empty
        };
    }

    private AgentMessage MarkAssistantWaitingForToolApproval(AgentMessage assistant, string? latestContent = null)
    {
        var content = ResolveAssistantBody(assistant, latestContent);
        _repository.UpdateMessage(assistant.Id, content, AgentMessageStatus.Pending, activityText: WaitingForToolApprovalText);
        return assistant with
        {
            Content = content,
            Status = AgentMessageStatus.Pending,
            ActivityText = WaitingForToolApprovalText,
            Error = string.Empty
        };
    }

    private AgentMessage MarkAssistantStoppedAfterCancellation(AgentMessage assistant, string? latestContent = null)
    {
        var content = ResolveAssistantBody(assistant, latestContent);
        _repository.UpdateMessage(assistant.Id, content, AgentMessageStatus.Complete, activityText: StoppedByUserActivityText);
        return assistant with
        {
            Content = content,
            Status = AgentMessageStatus.Complete,
            ActivityText = StoppedByUserActivityText,
            Error = string.Empty
        };
    }

    private void SkipPendingToolsForAssistant(long conversationId, long assistantMessageId, string reason)
    {
        var detail = _repository.GetConversation(conversationId);
        if (detail is null)
        {
            return;
        }

        foreach (var call in detail.ToolCalls.Where(t =>
            t.MessageId == assistantMessageId
            && t.ApprovalStatus == AgentToolApprovalStatus.Pending))
        {
            _repository.UpdateToolCall(
                call.Id,
                AgentToolApprovalStatus.Rejected,
                AgentToolExecutionStatus.Skipped,
                reason,
                string.Empty);
        }
    }

    private static string ResolveAssistantBody(AgentMessage assistant, string? latestContent)
    {
        var content = string.IsNullOrWhiteSpace(latestContent) ? assistant.Content : latestContent.Trim();
        return string.Equals(content, WaitingForToolApprovalText, StringComparison.Ordinal)
            || string.Equals(content, "正在思考...", StringComparison.Ordinal)
            ? string.Empty
            : content;
    }

    private AgentMessage? MarkAssistantStoppedAfterToolRejected(AgentToolCall call)
    {
        var detail = _repository.GetConversation(call.ConversationId);
        if (detail is null)
        {
            return null;
        }

        var assistant = ResolveFollowUpAssistantMessage(detail, call);
        if (assistant is null)
        {
            return null;
        }

        const string activity = "已拒绝该工具调用，任务已暂停。";
        _repository.UpdateMessageActivity(assistant.Id, AgentMessageStatus.Complete, activity);
        return assistant with
        {
            Status = AgentMessageStatus.Complete,
            ActivityText = activity,
            Error = string.Empty
        };
    }

    private AgentMessage? MarkAssistantStoppedAfterToolFailed(AgentToolCall call)
    {
        var detail = _repository.GetConversation(call.ConversationId);
        if (detail is null)
        {
            return null;
        }

        var assistant = ResolveFollowUpAssistantMessage(detail, call);
        if (assistant is null)
        {
            return null;
        }

        var activity = string.IsNullOrWhiteSpace(call.Error)
            ? "工具执行失败，任务已暂停。"
            : "工具执行失败，任务已暂停: " + call.Error;
        _repository.UpdateMessageActivity(assistant.Id, AgentMessageStatus.Complete, activity);
        return assistant with
        {
            Status = AgentMessageStatus.Complete,
            ActivityText = activity,
            Error = string.Empty
        };
    }

    private void ScheduleToolFollowUp(AgentToolCall call, long assistantMessageId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendToolFollowUpAsync(call.ConversationId, call, assistantMessageId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Follow-up is best effort; the approved tool result is already persisted.
            }
        });
    }

    private async Task<AgentSendResult> SendToolFollowUpAsync(
        long conversationId,
        AgentToolCall call,
        long assistantMessageId,
        CancellationToken cancellationToken)
    {
        var detail = _repository.GetConversation(conversationId);
        var settings = GetSettings();
        if (detail is null || ValidateSettings(settings) is not null)
        {
            return new AgentSendResult(false, null, null, Array.Empty<AgentToolCall>(), string.Empty);
        }

        AgentMessage? assistantMessage = null;
        var responseText = new StringBuilder();
        var lockHeld = false;
        try
        {
            detail = _repository.GetConversation(conversationId);
            assistantMessage = detail?.Messages.FirstOrDefault(m => m.Id == assistantMessageId && m.Role == AgentMessageRole.Assistant);
            if (detail is null || assistantMessage is null)
            {
                return new AgentSendResult(false, null, null, _repository.GetPendingToolCalls(conversationId), "找不到要更新的智能体回复");
            }

            await _agentRunLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockHeld = true;
            _activeConversationId = conversationId;
            _activeAssistantMessageId = assistantMessage.Id;
            _toolCallsThisTurn = 0;
            _pendingApprovalCall = null;
            _toolCallsByTurnSignature.Clear();

            var agent = await BuildAgentAsync(settings, AgentConversationMode.Chat, cancellationToken).ConfigureAwait(false);
            var preparedContext = await PrepareContextAsync(detail, settings, cancellationToken, includeUserBeforeAssistantId: assistantMessage.Id).ConfigureAwait(false);
            var session = await CreateSessionAsync(agent, preparedContext, cancellationToken).ConfigureAwait(false);
            var prompt =
                $"工具 {call.ToolName} 已执行完成。请基于以下结果继续完成用户上一条任务；" +
                "如果还需要调用工具可以继续调用。完成后用中文简洁说明结果，不要要求用户手动执行你能通过工具完成的步骤。\n" +
                call.ResultSummary;
            await foreach (var update in agent.RunStreamingAsync(
                    prompt,
                    session,
                    BuildRunOptions(settings, AgentConversationMode.Chat),
                    cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);
                }
            }

            if (_pendingApprovalCall is not null)
            {
                assistantMessage = MarkAssistantWaitingForToolApproval(assistantMessage, responseText.ToString());
                return new AgentSendResult(true, null, assistantMessage, GetToolCalls(conversationId), string.Empty);
            }

            var assistantText = responseText.Length == 0 ? "工具已执行完成。" : responseText.ToString().Trim();
            _repository.UpdateMessage(assistantMessage.Id, assistantText, AgentMessageStatus.Complete);
            assistantMessage = assistantMessage with
            {
                Content = assistantText,
                Status = AgentMessageStatus.Complete,
                ActivityText = string.Empty,
                Error = string.Empty
            };
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);
            return new AgentSendResult(true, null, assistantMessage, _repository.GetPendingToolCalls(conversationId), string.Empty);
        }
        catch (Exception ex) when (TryGetApprovalRequiredException(ex, out _))
        {
            if (assistantMessage is not null)
            {
                assistantMessage = MarkAssistantWaitingForToolApproval(assistantMessage, responseText.ToString());
            }

            return new AgentSendResult(true, null, assistantMessage, GetToolCalls(conversationId), string.Empty);
        }
        catch (Exception ex) when (IsAgentCallException(ex))
        {
            if (assistantMessage is not null)
            {
                var activity = "继续处理失败，任务已暂停: " + ex.Message;
                _repository.UpdateMessageActivity(assistantMessage.Id, AgentMessageStatus.Complete, activity);
                assistantMessage = assistantMessage with
                {
                    Status = AgentMessageStatus.Complete,
                    ActivityText = activity,
                    Error = string.Empty
                };
            }

            return new AgentSendResult(false, null, assistantMessage, _repository.GetPendingToolCalls(conversationId), ex.Message);
        }
        catch (Exception ex)
        {
            if (assistantMessage is not null)
            {
                var activity = "继续处理失败，任务已暂停: " + ex.Message;
                _repository.UpdateMessageActivity(assistantMessage.Id, AgentMessageStatus.Complete, activity);
                assistantMessage = assistantMessage with
                {
                    Status = AgentMessageStatus.Complete,
                    ActivityText = activity,
                    Error = string.Empty
                };
            }

            return new AgentSendResult(false, null, assistantMessage, _repository.GetPendingToolCalls(conversationId), ex.Message);
        }
        finally
        {
            _activeConversationId = 0;
            _activeAssistantMessageId = 0;
            _toolCallsThisTurn = 0;
            _pendingApprovalCall = null;
            _toolCallsByTurnSignature.Clear();
            if (lockHeld)
            {
                _agentRunLock.Release();
            }
        }
    }

    private static AgentMessage? ResolveFollowUpAssistantMessage(AgentConversationDetail detail, AgentToolCall call)
    {
        if (call.MessageId is long messageId)
        {
            var linked = detail.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == AgentMessageRole.Assistant);
            if (linked is not null)
            {
                return linked;
            }
        }

        return detail.Messages
            .Where(m => m.Role == AgentMessageRole.Assistant)
            .OrderByDescending(m => m.CreatedUtc)
            .ThenByDescending(m => m.Sequence)
            .FirstOrDefault();
    }

    private async Task<string> WriteToolChangesIfNeededAsync(
        ChannelWriter<AgentStreamEvent> writer,
        long conversationId,
        AgentMessage? userMessage,
        AgentMessage? assistantMessage,
        string previousSignature,
        CancellationToken cancellationToken)
    {
        var tools = GetToolCalls(conversationId);
        var signature = BuildToolSignature(tools);
        if (!string.Equals(signature, previousSignature, StringComparison.Ordinal))
        {
            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.ToolCallsChanged, userMessage, assistantMessage, string.Empty, tools, string.Empty),
                cancellationToken).ConfigureAwait(false);
        }

        return signature;
    }

    private IReadOnlyList<AgentToolCall> GetToolCalls(long conversationId)
        => _repository.GetConversation(conversationId)?.ToolCalls ?? Array.Empty<AgentToolCall>();

    private static string BuildToolSignature(IReadOnlyList<AgentToolCall> tools)
        => string.Join(
            "|",
            tools.Select(t => string.Join(
                ":",
                t.Id.ToString(),
                t.ApprovalStatus.ToString(),
                t.ExecutionStatus.ToString(),
                t.ResultSummary.Length.ToString(),
                t.Error.Length.ToString())));

    private AgentStreamEvent FailedStreamEvent(
        AgentMessage? userMessage,
        AgentMessage? assistantMessage,
        long conversationId,
        string error)
        => new(
            AgentStreamEventKind.Failed,
            userMessage,
            assistantMessage,
            string.Empty,
            conversationId > 0 ? GetToolCalls(conversationId) : Array.Empty<AgentToolCall>(),
            error);

    private async Task<ChatClientAgent> BuildAgentAsync(AgentSettings settings, AgentConversationMode mode, CancellationToken cancellationToken)
    {
        var chatClient = CreateChatClient(settings);
        var tools = await BuildToolsAsync(mode, settings, cancellationToken).ConfigureAwait(false);
        var options = new ChatClientAgentOptions
        {
            Name = "EdgeKit",
            Description = "EdgeKit 桌面智能体",
            ChatOptions = new ChatOptions
            {
                ModelId = settings.Model,
                Temperature = (float)settings.Temperature,
                Instructions = BuildInstructions(mode, settings),
                Tools = tools.Cast<AITool>().ToList(),
                AllowMultipleToolCalls = false,
                ToolMode = new AutoChatToolMode()
            }
        };

        return new ChatClientAgent(chatClient, options, null, null);
    }

    private IChatClient CreateChatClient(AgentSettings settings)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl)
        };
        var nativeClient = new ChatClient(settings.Model, new ApiKeyCredential(settings.ApiKey), options);
        var client = nativeClient.AsIChatClient();
        return client
            .AsBuilder()
            .UseFunctionInvocation(null, ConfigureFunctionInvocation)
            .Build(null);
    }

    private void ConfigureFunctionInvocation(FunctionInvokingChatClient client)
    {
        client.AllowConcurrentInvocation = false;
        client.FunctionInvoker = async (context, cancellationToken) =>
        {
            try
            {
                return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (TryGetApprovalRequiredException(ex, out _))
            {
                context.Terminate = true;
                return WaitingForToolApprovalText;
            }
        };
    }

    private async Task<IList<AIFunction>> BuildToolsAsync(AgentConversationMode mode, AgentSettings settings, CancellationToken cancellationToken)
    {
        var descriptors = _toolRegistry.GetTools(mode, settings)
            .Where(d => _toolExecutor.ShouldExposeTool(d, settings))
            .ToArray();
        var result = new List<AIFunction>();

        foreach (var descriptor in descriptors)
        {
            result.Add(new AgentRuntimeFunction(
                descriptor.Id,
                descriptor.Description,
                AgentToolSchemas.ForTool(descriptor.Id),
                JsonOptions,
                (arguments, cancellationToken) => InvokeToolAsync(descriptor.Id, ToJsonElement(arguments), cancellationToken)));
        }

        if (mode != AgentConversationMode.Translate)
        {
            result.AddRange(await _mcpTools.BuildToolsAsync(settings, InvokeMcpToolAsync, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<string> InvokeToolAsync(string toolId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (_activeConversationId <= 0)
        {
            return "工具调用失败：没有活动会话。";
        }

        if (++_toolCallsThisTurn > MaxToolCallsPerTurn)
        {
            return "工具调用已达到本轮上限。";
        }

        var settings = GetSettings();
        var descriptor = _toolExecutor.Find(toolId);
        descriptor ??= _mcpTools.FindDescriptor(toolId, settings);
        if (descriptor is null)
        {
            return "未知工具: " + toolId;
        }

        var argsJson = NormalizeToolArgumentsJson(arguments);
        var argsSummary = AgentToolExecutor.ArgumentsSummary(arguments);
        var signature = BuildToolCallSignature(_activeAssistantMessageId, descriptor.Id, argsJson);
        if (_toolCallsByTurnSignature.TryGetValue(signature, out var existingCallId))
        {
            var existing = _repository.GetToolCall(existingCallId);
            if (existing is not null)
            {
                ThrowIfPendingApproval(existing);
                return FormatDuplicateToolCallResult(existing);
            }
        }

        var existingPersistedCall = FindExistingEquivalentToolCall(_activeConversationId, _activeAssistantMessageId, descriptor.Id, argsJson);
        if (existingPersistedCall is not null)
        {
            _toolCallsByTurnSignature[signature] = existingPersistedCall.Id;
            ThrowIfPendingApproval(existingPersistedCall);
            return FormatDuplicateToolCallResult(existingPersistedCall);
        }

        var approval = (_toolExecutor.CanAutoExecute(descriptor, settings)
                || _toolExecutor.IsTrustedToolCall(descriptor, arguments, settings))
            ? AgentToolApprovalStatus.NotRequired
            : AgentToolApprovalStatus.Pending;
        var execution = approval == AgentToolApprovalStatus.Pending
            ? AgentToolExecutionStatus.Pending
            : AgentToolExecutionStatus.Running;

        var call = _repository.AddToolCall(
            _activeConversationId,
            _activeAssistantMessageId > 0 ? _activeAssistantMessageId : null,
            descriptor.Id,
            descriptor.Name,
            descriptor.Risk,
            argsJson,
            argsSummary,
            approval,
            execution);
        _toolCallsByTurnSignature[signature] = call.Id;
        if (approval == AgentToolApprovalStatus.Pending)
        {
            _pendingApprovalCall = call;
            await PublishToolCallsChangedAsync(GetWaitingAssistantMessage(), cancellationToken).ConfigureAwait(false);
            throw new AgentToolApprovalRequiredException(call);
        }

        await PublishToolCallsChangedAsync(GetRunningAssistantMessage(descriptor), cancellationToken).ConfigureAwait(false);
        var result = await _toolExecutor.ExecuteAsync(toolId, arguments, cancellationToken).ConfigureAwait(false);
        var updated = _repository.UpdateToolCall(
            call.Id,
            AgentToolApprovalStatus.NotRequired,
            result.Success ? AgentToolExecutionStatus.Succeeded : AgentToolExecutionStatus.Failed,
            result.Success ? result.Result : string.Empty,
            result.Error);
        _repository.AddMessage(
            _activeConversationId,
            AgentMessageRole.Tool,
            result.Success ? result.Result : "工具执行失败: " + result.Error,
            result.Success ? AgentMessageStatus.Complete : AgentMessageStatus.Failed,
            result.Error);
        await PublishToolCallsChangedAsync(GetRunningAssistantMessage(descriptor, updated), cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Result : "工具执行失败: " + result.Error;
    }

    private AgentMessage? GetWaitingAssistantMessage()
    {
        var context = _activeStreamContext;
        if (context?.AssistantMessage is null)
        {
            return null;
        }

        var assistant = context.AssistantMessage with
        {
            Status = AgentMessageStatus.Pending,
            ActivityText = WaitingForToolApprovalText,
            Error = string.Empty
        };
        _activeStreamContext = context with { AssistantMessage = assistant };
        return assistant;
    }

    private AgentMessage? GetRunningAssistantMessage(AgentToolDescriptor descriptor, AgentToolCall? call = null)
    {
        var context = _activeStreamContext;
        if (context?.AssistantMessage is null)
        {
            return null;
        }

        var activity = call?.ExecutionStatus switch
        {
            AgentToolExecutionStatus.Succeeded => $"工具 {descriptor.Name} 已执行完成，正在继续处理...",
            AgentToolExecutionStatus.Failed => $"工具 {descriptor.Name} 执行失败，正在继续处理...",
            _ => $"正在执行工具：{descriptor.Name}..."
        };
        _repository.UpdateMessageActivity(context.AssistantMessage.Id, AgentMessageStatus.Pending, activity);
        var assistant = context.AssistantMessage with
        {
            Status = AgentMessageStatus.Pending,
            ActivityText = activity,
            Error = string.Empty
        };
        _activeStreamContext = context with { AssistantMessage = assistant };
        return assistant;
    }

    private async Task PublishToolCallsChangedAsync(AgentMessage? assistantMessage, CancellationToken cancellationToken)
    {
        var context = _activeStreamContext;
        if (context is null || _activeConversationId <= 0)
        {
            return;
        }

        await context.Writer.WriteAsync(
            new AgentStreamEvent(
                AgentStreamEventKind.ToolCallsChanged,
                context.UserMessage,
                assistantMessage ?? context.AssistantMessage,
                string.Empty,
                GetToolCalls(_activeConversationId),
                string.Empty),
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildToolCallSignature(long assistantMessageId, string toolId, string argumentsJson)
        => assistantMessageId.ToString(CultureInfo.InvariantCulture) + "|" + toolId.ToLowerInvariant() + "|" + argumentsJson;

    private static string NormalizeToolArgumentsJson(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(
            JsonSerializer.Deserialize<SortedDictionary<string, JsonElement>>(arguments.GetRawText(), JsonOptions)
                ?? new SortedDictionary<string, JsonElement>(),
            JsonOptions);
    }

    private static string FormatDuplicateToolCallResult(AgentToolCall call)
    {
        if (call.ExecutionStatus == AgentToolExecutionStatus.Succeeded && !string.IsNullOrWhiteSpace(call.ResultSummary))
        {
            return "已复用相同工具调用结果，未重复执行。" + Environment.NewLine + call.ResultSummary;
        }

        if (call.ApprovalStatus == AgentToolApprovalStatus.Pending)
        {
            return $"相同工具调用已在等待用户确认。调用编号: {call.Id}";
        }

        if (!string.IsNullOrWhiteSpace(call.Error))
        {
            return "相同工具调用已执行失败，未重复执行: " + call.Error;
        }

        return "相同工具调用已存在，未重复执行。";
    }

    private void ThrowIfPendingApproval(AgentToolCall call)
    {
        if (call.ApprovalStatus == AgentToolApprovalStatus.Pending)
        {
            _pendingApprovalCall = call;
            throw new AgentToolApprovalRequiredException(call);
        }
    }

    private AgentToolCall? FindExistingEquivalentToolCall(long conversationId, long assistantMessageId, string toolId, string argumentsJson)
    {
        if (assistantMessageId <= 0)
        {
            return null;
        }

        var detail = _repository.GetConversation(conversationId);
        return detail?.ToolCalls
            .Where(call => call.MessageId == assistantMessageId
                && call.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeStoredToolArgumentsJson(call.ArgumentsJson), argumentsJson, StringComparison.Ordinal))
            .OrderByDescending(call => call.CreatedUtc)
            .FirstOrDefault();
    }

    private static string NormalizeStoredToolArgumentsJson(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return NormalizeToolArgumentsJson(document.RootElement);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    private Task<string> InvokeMcpToolAsync(string toolId, AIFunction tool, JsonElement arguments, CancellationToken cancellationToken)
        => InvokeToolAsync(toolId, arguments, cancellationToken);

    private static JsonElement ToJsonElement(AIFunctionArguments arguments)
        => JsonSerializer.SerializeToElement(
            arguments.ToDictionary(pair => pair.Key, pair => NormalizeArgumentValue(pair.Value), StringComparer.Ordinal),
            JsonOptions);

    private static object? NormalizeArgumentValue(object? value)
        => value switch
        {
            JsonElement element => element.ValueKind == JsonValueKind.Undefined ? null : element.Clone(),
            _ => value
        };

    private ChatClientAgentRunOptions BuildRunOptions(AgentSettings settings, AgentConversationMode mode)
        => new(new ChatOptions
        {
            ModelId = settings.Model,
            Temperature = (float)settings.Temperature,
            Instructions = BuildInstructions(mode, settings),
            AllowMultipleToolCalls = false,
            ToolMode = new AutoChatToolMode()
        });

    private async Task<AgentSession> CreateSessionAsync(
        ChatClientAgent agent,
        AgentContextPackage context,
        CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        session.SetInMemoryChatHistory(context.Messages.ToList(), null, JsonOptions);
        return session;
    }

    private async Task<AgentContextPackage> PrepareContextAsync(
        AgentConversationDetail detail,
        AgentSettings settings,
        CancellationToken cancellationToken,
        ChannelWriter<AgentStreamEvent>? writer = null,
        AgentMessage? userMessage = null,
        AgentMessage? assistantMessage = null,
        long includeUserBeforeAssistantId = 0,
        long excludeMessageId = 0)
    {
        var package = BuildContextPackage(detail, settings, includeUserBeforeAssistantId, excludeMessageId);
        var compressionLimit = (int)Math.Ceiling(settings.ContextWindowTokens * ContextCompressionThreshold);
        if (package.Status.EstimatedTokens < compressionLimit
            || package.VisibleEntries.Count <= ContextRecentLedgerItemsToKeep)
        {
            return package;
        }

        var conversationId = detail.Conversation.Id;
        lock (_compressingConversationIds)
        {
            _compressingConversationIds.Add(conversationId);
        }

        if (writer is not null)
        {
            await writer.WriteAsync(
                new AgentStreamEvent(
                    AgentStreamEventKind.ContextChanged,
                    userMessage,
                    assistantMessage,
                    string.Empty,
                    GetToolCalls(conversationId),
                    string.Empty,
                    package.Status with { IsCompressing = true }),
                CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            var summary = await CompressContextAsync(package, settings, cancellationToken).ConfigureAwait(false);
            if (summary is not null)
            {
                detail = _repository.GetConversation(conversationId) ?? detail;
                package = BuildContextPackage(detail, settings, includeUserBeforeAssistantId, excludeMessageId);
            }
        }
        finally
        {
            lock (_compressingConversationIds)
            {
                _compressingConversationIds.Remove(conversationId);
            }
        }

        if (writer is not null)
        {
            await writer.WriteAsync(
                new AgentStreamEvent(
                    AgentStreamEventKind.ContextChanged,
                    userMessage,
                    assistantMessage,
                    string.Empty,
                    GetToolCalls(conversationId),
                    string.Empty,
                    package.Status),
                CancellationToken.None).ConfigureAwait(false);
        }

        return package;
    }

    private async Task<AgentContextSummary?> CompressContextAsync(
        AgentContextPackage package,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        var compressibleEntries = package.VisibleEntries
            .Take(Math.Max(0, package.VisibleEntries.Count - ContextRecentLedgerItemsToKeep))
            .ToArray();
        if (compressibleEntries.Length == 0)
        {
            return null;
        }

        var source = BuildCompressionSource(package.Summary, compressibleEntries);
        var summary = await GenerateContextSummaryAsync(settings, source, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = BuildLocalContextSummary(package.Summary, compressibleEntries);
        }

        var messageCutoff = Math.Max(
            package.Summary?.SourceMessageSequence ?? 0,
            compressibleEntries.Max(e => e.MessageSequence));
        var toolCutoff = Math.Max(
            package.Summary?.SourceToolCallId ?? 0,
            compressibleEntries.Max(e => e.ToolCallId));
        return _repository.SaveContextSummary(
            package.Detail.Conversation.Id,
            summary.Trim(),
            messageCutoff,
            toolCutoff,
            EstimateTokens(summary));
    }

    private async Task<string> GenerateContextSummaryAsync(
        AgentSettings settings,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(settings.BaseUrl)
            };
            var nativeClient = new ChatClient(settings.Model, new ApiKeyCredential(settings.ApiKey), options);
            var client = nativeClient.AsIChatClient();
            var response = await client.GetResponseAsync(
                [
                    new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.System,
                        "你负责压缩 EdgeKit 智能体的旧会话上下文。必须保留用户意图、已给出的结论、未完成事项、工具调用的成功/失败/错误信息、关键路径/命令/数值。用中文结构化摘要，不要编造。"),
                    new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.User,
                        "请压缩以下旧上下文，供后续对话继续使用：" + Environment.NewLine + TrimForPrompt(source, 120000))
                ],
                new ChatOptions
                {
                    ModelId = settings.Model,
                    Temperature = 0.1f
                },
                cancellationToken).ConfigureAwait(false);
            return response.Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private AgentContextPackage BuildContextPackage(
        AgentConversationDetail detail,
        AgentSettings settings,
        long? includeUserBeforeAssistantId,
        long? excludeMessageId = null)
    {
        var summary = detail.ContextSummaries
            .OrderByDescending(s => s.UpdatedUtc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefault();
        var entries = BuildContextLedgerEntries(detail, includeUserBeforeAssistantId, excludeMessageId).ToArray();
        var visibleEntries = entries
            .Where(e => !IsCoveredBySummary(e, summary))
            .ToArray();

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        var summaryContext = string.Empty;
        if (summary is not null && !string.IsNullOrWhiteSpace(summary.Summary))
        {
            summaryContext =
                "以下是较早会话上下文的自动压缩摘要。后续对话需要把它当作真实历史背景使用：" +
                Environment.NewLine +
                summary.Summary.Trim();
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                ChatRole.System,
                summaryContext));
        }

        foreach (var entry in visibleEntries)
        {
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(entry.Role, entry.Content));
        }

        var instructions = BuildInstructions(AgentConversationMode.Chat, settings);
        var toolDescriptors = _toolRegistry.GetTools(AgentConversationMode.Chat, settings)
            .Where(d => _toolExecutor.ShouldExposeTool(d, settings))
            .ToArray();
        var toolSchemasJson = string.Join("\n", toolDescriptors.Select(d => AgentToolSchemas.ForTool(d.Id).GetRawText()));

        var estimatedTokens = messages.Sum(m => EstimateTokens(m.Text ?? string.Empty) + 8)
            + EstimateTokens(instructions) + 8
            + EstimateTokens(toolSchemasJson) + 8;
        var preview = BuildContextPreview(messages);
        var compressing = IsCompressionInProgress(detail.Conversation.Id);

        int systemTokens = 0, toolsTokens = 0, conversationTokens = 0;
        if (!string.IsNullOrWhiteSpace(summaryContext))
        {
            systemTokens += EstimateTokens(summaryContext) + 8;
        }

        foreach (var entry in visibleEntries)
        {
            var tokens = EstimateTokens(entry.Content) + 8;
            if (entry.IsTool)
            {
                toolsTokens += tokens;
            }
            else if (entry.Role == ChatRole.System)
            {
                systemTokens += tokens;
            }
            else
            {
                conversationTokens += tokens;
            }
        }

        var instructionsTokens = EstimateTokens(instructions) + 8;
        var toolDefinitionsTokens = EstimateTokens(toolSchemasJson) + 8;
        systemTokens += instructionsTokens + toolDefinitionsTokens;

        var status = new AgentContextStatus(
            detail.Conversation.Id,
            estimatedTokens,
            settings.ContextWindowTokens,
            settings.ContextWindowTokens <= 0 ? 0 : Math.Min(1, estimatedTokens / (double)settings.ContextWindowTokens),
            detail.Messages.Count(m => m.Role is AgentMessageRole.User or AgentMessageRole.Assistant),
            detail.ToolCalls.Count,
            summary is null ? 0 : 1,
            compressing,
            summary?.UpdatedUtc,
            preview,
            systemTokens,
            toolsTokens,
            conversationTokens,
            instructionsTokens,
            toolDefinitionsTokens);
        return new AgentContextPackage(detail, messages, status, summary, visibleEntries);
    }

    private static IEnumerable<ContextLedgerEntry> BuildContextLedgerEntries(
        AgentConversationDetail detail,
        long? includeUserBeforeAssistantId,
        long? excludeMessageId)
    {
        var entries = new List<ContextLedgerEntry>();
        foreach (var message in detail.Messages.Where(m => m.Role != AgentMessageRole.Tool))
        {
            if (excludeMessageId is > 0 && message.Id == excludeMessageId)
            {
                continue;
            }

            var content = FormatMessageForContext(message);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            entries.Add(new ContextLedgerEntry(
                message.CreatedUtc,
                message.Sequence,
                0,
                ToChatRole(message.Role),
                content,
                IsTool: false));
        }

        foreach (var tool in detail.ToolCalls)
        {
            entries.Add(new ContextLedgerEntry(
                tool.CreatedUtc,
                0,
                checked((int)Math.Min(tool.Id, int.MaxValue)),
                ChatRole.System,
                FormatToolCallForContext(tool),
                IsTool: true));
        }

        if (includeUserBeforeAssistantId is > 0
            && detail.Messages.FirstOrDefault(m => m.Id == includeUserBeforeAssistantId) is { } assistant)
        {
            var sourceUser = detail.Messages
                .Where(m => m.Role == AgentMessageRole.User && m.Sequence < assistant.Sequence)
                .OrderByDescending(m => m.Sequence)
                .ThenByDescending(m => m.Id)
                .FirstOrDefault();
            if (sourceUser is not null && entries.All(e => e.MessageSequence != sourceUser.Sequence))
            {
                entries.Add(new ContextLedgerEntry(
                    sourceUser.CreatedUtc,
                    sourceUser.Sequence,
                    0,
                    ChatRole.User,
                    sourceUser.Content.Trim(),
                    IsTool: false));
            }
        }

        return entries
            .OrderBy(e => e.CreatedUtc)
            .ThenBy(e => e.MessageSequence == 0 ? int.MaxValue : e.MessageSequence)
            .ThenBy(e => e.ToolCallId);
    }

    private static string FormatMessageForContext(AgentMessage message)
    {
        if (message.Role == AgentMessageRole.User)
        {
            return string.IsNullOrWhiteSpace(message.Content)
                ? string.Empty
                : TrimForPrompt(message.Content.Trim(), MessageContextMaxChars);
        }

        if (message.Role == AgentMessageRole.Assistant)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(message.Content)
                && !string.Equals(message.Content.Trim(), WaitingForToolApprovalText, StringComparison.Ordinal)
                && !string.Equals(message.Content.Trim(), "正在思考...", StringComparison.Ordinal))
            {
                parts.Add(TrimForPrompt(message.Content.Trim(), MessageContextMaxChars));
            }

            if (!string.IsNullOrWhiteSpace(message.ActivityText))
            {
                parts.Add("【运行状态】" + message.ActivityText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(message.Error))
            {
                parts.Add("【回复错误】" + TrimForPrompt(message.Error.Trim(), 1200));
            }

            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }

        return string.Empty;
    }

    private static string FormatToolCallForContext(AgentToolCall tool)
    {
        var detail = string.IsNullOrWhiteSpace(tool.Error)
            ? tool.ResultSummary
            : tool.Error;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = tool.ResultSummary;
        }

        var builder = new StringBuilder();
        builder.AppendLine("【工具调用记录】");
        builder.AppendLine("（以下为系统内部记录，仅供你参考，禁止原样复述给用户）");
        builder.AppendLine("工具: " + tool.ToolName + " (" + tool.ToolId + ")");
        builder.AppendLine("风险: " + tool.Risk);
        builder.AppendLine("审批状态: " + tool.ApprovalStatus);
        builder.AppendLine("执行状态: " + tool.ExecutionStatus);
        builder.AppendLine("参数摘要: " + EmptyFallback(TrimForPrompt(tool.ArgumentsSummary, ToolContextSummaryMaxChars)));
        builder.AppendLine("结果/错误摘要: " + EmptyFallback(TrimForPrompt(detail, ToolContextSummaryMaxChars)));
        return builder.ToString().Trim();
    }

    private static bool IsCoveredBySummary(ContextLedgerEntry entry, AgentContextSummary? summary)
    {
        if (summary is null)
        {
            return false;
        }

        return entry.IsTool
            ? entry.ToolCallId > 0 && entry.ToolCallId <= summary.SourceToolCallId
            : entry.MessageSequence > 0 && entry.MessageSequence <= summary.SourceMessageSequence;
    }

    private static string BuildCompressionSource(
        AgentContextSummary? previousSummary,
        IReadOnlyList<ContextLedgerEntry> entries)
    {
        var builder = new StringBuilder();
        if (previousSummary is not null && !string.IsNullOrWhiteSpace(previousSummary.Summary))
        {
            builder.AppendLine("【已有压缩摘要】");
            builder.AppendLine(previousSummary.Summary.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("【新增待压缩上下文】");
        foreach (var entry in entries)
        {
            builder.AppendLine(entry.Role + ":");
            builder.AppendLine(entry.Content);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildLocalContextSummary(
        AgentContextSummary? previousSummary,
        IReadOnlyList<ContextLedgerEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("自动压缩摘要（本地兜底）：");
        if (previousSummary is not null && !string.IsNullOrWhiteSpace(previousSummary.Summary))
        {
            builder.AppendLine(previousSummary.Summary.Trim());
        }

        foreach (var entry in entries)
        {
            builder.AppendLine("- " + entry.Role + ": " + TrimForPrompt(entry.Content.ReplaceLineEndings(" "), 800));
        }

        return builder.ToString();
    }

    private static string BuildContextPreview(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        // 预览不再展示整段上下文原文，避免长会话拼接超长字符串导致 UI 卡死/闪退。
        // 用量信息已通过 AgentContextStatus 的 token/消息计数与进度条呈现。
        return string.Empty;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var nonAscii = 0;
        foreach (var ch in text)
        {
            if (ch <= 0x7f)
            {
                ascii++;
            }
            else
            {
                nonAscii++;
            }
        }

        return Math.Max(1, nonAscii + (int)Math.Ceiling(ascii / 4d));
    }

    private bool IsCompressionInProgress(long conversationId)
    {
        lock (_compressingConversationIds)
        {
            return _compressingConversationIds.Contains(conversationId);
        }
    }

    private Task SaveSessionAsync(
        ChatClientAgent agent,
        AgentSession session,
        long conversationId,
        CancellationToken cancellationToken)
    {
        _ = agent;
        _ = session;
        _ = cancellationToken;
        try
        {
            _repository.SaveSession(conversationId, string.Empty);
        }
        catch
        {
            // Session cleanup is best effort; messages are still stored.
        }

        return Task.CompletedTask;
    }

    private static string? BuildContinuationPromptIfNeeded(AgentConversationDetail detail, string userMessage, long currentUserMessageId = 0)
    {
        if (!IsContinuationRequest(userMessage))
        {
            return null;
        }

        var target = FindContinuationTarget(detail, currentUserMessageId);
        if (target is null)
        {
            return null;
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("用户要求继续上一轮上下文。请续接最近未完成或最近一条回复，不要跳回更早话题，不要从头重复已写内容。");
        if (target.SourceUser is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine("最近要继续的用户问题:");
            prompt.AppendLine(target.SourceUser.Content.Trim());
        }

        if (target.Assistant is not null && !string.IsNullOrWhiteSpace(target.Assistant.Content))
        {
            prompt.AppendLine();
            prompt.AppendLine("已经生成的回复正文，请从末尾继续:");
            prompt.AppendLine(target.Assistant.Content.Trim());
        }

        if (target.ToolSummaries.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("相关工具调用记录，包含成功或失败结果。请基于这些记录继续，避免重复调用相同工具:");
            foreach (var toolResult in target.ToolSummaries)
            {
                prompt.AppendLine(toolResult);
            }
        }

        prompt.AppendLine();
        prompt.AppendLine("现在请继续完成最近这轮任务。");
        return prompt.ToString();
    }

    private static ContinuationTarget? FindContinuationTarget(AgentConversationDetail detail, long currentUserMessageId)
    {
        var orderedMessages = detail.Messages
            .Where(m => m.Role is AgentMessageRole.User or AgentMessageRole.Assistant)
            .Where(m => currentUserMessageId <= 0 || m.Id != currentUserMessageId)
            .OrderBy(m => m.Sequence)
            .ThenBy(m => m.Id)
            .ToArray();

        var assistants = orderedMessages
            .Where(m => m.Role == AgentMessageRole.Assistant)
            .OrderByDescending(m => m.Sequence)
            .ThenByDescending(m => m.Id)
            .ToArray();

        var recentAssistant = assistants.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.Content)
            || !string.IsNullOrWhiteSpace(m.ActivityText)
            || !string.IsNullOrWhiteSpace(m.Error));
        var recentTool = detail.ToolCalls
            .OrderByDescending(t => t.CreatedUtc)
            .ThenByDescending(t => t.Id)
            .FirstOrDefault();
        if (recentTool is not null)
        {
            var toolAssistant = recentTool.MessageId is long assistantId
                ? assistants.FirstOrDefault(m => m.Id == assistantId)
                : null;
            var toolIsLatestTurn = recentAssistant is null
                || recentTool.MessageId == recentAssistant.Id
                || recentTool.CreatedUtc >= recentAssistant.CreatedUtc;
            if (toolIsLatestTurn)
            {
                var sourceUser = FindSourceUser(orderedMessages, toolAssistant);
                sourceUser ??= orderedMessages
                    .Where(m => m.Role == AgentMessageRole.User)
                    .OrderByDescending(m => m.Sequence)
                    .ThenByDescending(m => m.Id)
                    .FirstOrDefault();
                return new ContinuationTarget(
                    sourceUser,
                    toolAssistant,
                    BuildToolSummariesForAssistant(detail.ToolCalls, toolAssistant?.Id, recentTool.Id));
            }
        }

        if (recentAssistant is not null)
        {
            return new ContinuationTarget(
                FindSourceUser(orderedMessages, recentAssistant),
                recentAssistant,
                BuildToolSummariesForAssistant(detail.ToolCalls, recentAssistant.Id, null));
        }

        var recentUser = orderedMessages
            .Where(m => m.Role == AgentMessageRole.User)
            .OrderByDescending(m => m.Sequence)
            .ThenByDescending(m => m.Id)
            .FirstOrDefault();
        return recentUser is null
            ? null
            : new ContinuationTarget(recentUser, null, Array.Empty<string>());
    }

    private static AgentMessage? FindSourceUser(IReadOnlyList<AgentMessage> orderedMessages, AgentMessage? assistant)
    {
        if (assistant is null)
        {
            return null;
        }

        return orderedMessages
            .Where(m => m.Role == AgentMessageRole.User && m.Sequence < assistant.Sequence)
            .OrderByDescending(m => m.Sequence)
            .ThenByDescending(m => m.Id)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> BuildToolSummariesForAssistant(
        IReadOnlyList<AgentToolCall> toolCalls,
        long? assistantMessageId,
        long? fallbackToolId)
    {
        var tools = toolCalls
            .Where(t => assistantMessageId is > 0
                ? t.MessageId == assistantMessageId
                : fallbackToolId is not null && t.Id == fallbackToolId)
            .OrderBy(t => t.CreatedUtc)
            .Select(t => $"{t.ToolName}: {TrimForPrompt(FormatToolCallForContext(t), 1600)}")
            .ToArray();
        if (tools.Length > 0 || fallbackToolId is null)
        {
            return tools;
        }

        return toolCalls
            .Where(t => t.Id == fallbackToolId)
            .Select(t => $"{t.ToolName}: {TrimForPrompt(FormatToolCallForContext(t), 1600)}")
            .ToArray();
    }

    private static bool IsContinuationRequest(string message)
    {
        var normalized = message.Trim()
            .Trim('。', '.', '！', '!', '？', '?', '~', '～')
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized is "继续"
            or "接着"
            or "继续说"
            or "接着说"
            or "往下"
            or "往下说"
            or "继续回复"
            or "继续生成"
            or "接着生成"
            or "说下去"
            or "继续上面"
            or "继续刚才"
            or "continue"
            or "goon";
    }

    private static string TrimForPrompt(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + Environment.NewLine + "...";

    private static string EmptyFallback(string value)
        => string.IsNullOrWhiteSpace(value) ? "无" : value;

    private static ChatRole ToChatRole(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.System => ChatRole.System,
            AgentMessageRole.Assistant => ChatRole.Assistant,
            AgentMessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User
        };

    private static string? ValidateSettings(AgentSettings settings)
    {
        if (!settings.Enabled)
        {
            return "AI 智能体未启用，请先在设置中开启。";
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return "请先配置 API Key。";
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return "请先配置模型名称。";
        }

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _))
        {
            return "Base URL 无效。";
        }

        return null;
    }

    private async Task<string> GenerateConversationTitleAsync(string firstUserMessage, CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        if (ValidateSettings(settings) is not null)
        {
            return string.Empty;
        }

        try
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(settings.BaseUrl)
            };
            var nativeClient = new ChatClient(settings.Model, new ApiKeyCredential(settings.ApiKey), options);
            var client = nativeClient.AsIChatClient();
            var response = await client.GetResponseAsync(
                [
                    new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.System,
                        "你只负责给对话生成标题。标题必须是中文，4 到 16 个字，不要标点、引号、编号或解释。"),
                    new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.User,
                        "用户第一条消息：" + Environment.NewLine + firstUserMessage.Trim())
                ],
                new ChatOptions
                {
                    ModelId = settings.Model,
                    Temperature = 0.2f
                },
                cancellationToken).ConfigureAwait(false);
            return response.Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsDefaultConversationTitle(string title)
        => string.IsNullOrWhiteSpace(title)
            || string.Equals(title.Trim(), "新对话", StringComparison.Ordinal)
            || string.Equals(title.Trim(), "翻译会话", StringComparison.Ordinal)
            || string.Equals(title.Trim(), "Windows 配置", StringComparison.Ordinal);

    private static string NormalizeConversationTitle(string title)
    {
        var normalized = title.Trim()
            .Trim('"', '\'', '“', '”', '‘', '’', '《', '》')
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        normalized = new string(normalized.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (normalized.EndsWith("。", StringComparison.Ordinal)
            || normalized.EndsWith(".", StringComparison.Ordinal)
            || normalized.EndsWith("！", StringComparison.Ordinal)
            || normalized.EndsWith("!", StringComparison.Ordinal)
            || normalized.EndsWith("？", StringComparison.Ordinal)
            || normalized.EndsWith("?", StringComparison.Ordinal))
        {
            normalized = normalized[..^1].Trim();
        }

        return normalized.Length <= 16 ? normalized : normalized[..16];
    }

    private static string BuildFallbackConversationTitle(string firstUserMessage)
    {
        var title = NormalizeConversationTitle(firstUserMessage);
        if (title.Length >= 4)
        {
            return title;
        }

        return string.IsNullOrWhiteSpace(title) ? "新对话" : title;
    }

    private static bool IsAgentCallException(Exception exception)
        => exception is HttpRequestException
            or TaskCanceledException
            or InvalidOperationException
            or ClientResultException
            or ArgumentException
            or NotSupportedException
            or JsonException;

    private static bool TryGetApprovalRequiredException(Exception exception, out AgentToolApprovalRequiredException approvalException)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AgentToolApprovalRequiredException found)
            {
                approvalException = found;
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (TryGetApprovalRequiredException(inner, out approvalException))
                    {
                        return true;
                    }
                }
            }
        }

        approvalException = null!;
        return false;
    }

    private static string BuildInstructions(AgentConversationMode mode, AgentSettings settings)
    {
        var modeText = mode switch
        {
            AgentConversationMode.Translate =>
                "当前模式是翻译。优先完成翻译，保留格式和语气。用户没有要求解释时，不要额外展开。",
            AgentConversationMode.WindowsConfig =>
                "当前模式是 Windows 配置。你可以调用 EdgeKit 提供的白名单工具读取或修改系统配置。任何写操作、删除、结束进程都必须等待用户确认工具卡片。",
            _ =>
                "当前模式是通用对话。可以解释问题、整理步骤，并在需要时调用安全的只读工具。"
        };

        var actionText = settings.ActionMode switch
        {
            AgentActionMode.SuggestOnly => "动作模式：只给建议，不主动执行写操作。",
            AgentActionMode.AutoWithWhitelist => "动作模式：白名单自动化；只读工具可自动执行，命中可信目录或命令白名单的写入/命令可自动执行，其余动作必须等待用户确认工具卡片。",
            _ => "动作模式：执行写操作前必须确认。"
        };

        var toolText =
            "可用工具能力：" +
            (settings.EnableFileTools ? " 文件读写/搜索;" : string.Empty) +
            (settings.EnableShellTools ? " Shell 命令;" : string.Empty) +
            (settings.EnableWebTools ? " Web 搜索/网页读取;" : string.Empty) +
            (settings.EnableMcpTools ? " MCP 配置已启用但外部 MCP 运行时可能需要单独适配;" : string.Empty);

        return
            "你是 EdgeKit 桌面智能体，用中文简洁回答。" + Environment.NewLine +
            modeText + Environment.NewLine +
            actionText + Environment.NewLine +
            toolText + Environment.NewLine +
            "选择工具时优先使用最小权限、最贴合任务的专用工具；读取文件或目录信息优先使用文件只读工具，只有专用工具无法满足时再使用 Shell。" + Environment.NewLine +
            "同一轮对话里不要对同一工具和同一参数重复调用；已有工具结果足够时直接基于结果回答。" + Environment.NewLine +
            "不要在回复中输出或复述\u201c【工具调用记录】\u201d\u201c【运行状态】\u201d等系统内部元信息块；工具结果请用自然语言转述。" + Environment.NewLine +
            "不要声称已经执行未经过工具结果确认的操作。不要要求用户运行任意脚本，除非是在解释手动步骤。";
    }

    private sealed class AgentToolApprovalRequiredException : Exception
    {
        public AgentToolApprovalRequiredException(AgentToolCall call)
            : base("工具调用等待用户确认。")
        {
            ToolCall = call;
        }

        public AgentToolCall ToolCall { get; }
    }

    private sealed record AgentStreamContext(
        ChannelWriter<AgentStreamEvent> Writer,
        AgentMessage? UserMessage,
        AgentMessage? AssistantMessage,
        CancellationToken CancellationToken);

    private sealed record AgentContextPackage(
        AgentConversationDetail Detail,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> Messages,
        AgentContextStatus Status,
        AgentContextSummary? Summary,
        IReadOnlyList<ContextLedgerEntry> VisibleEntries);

    private sealed record ContextLedgerEntry(
        DateTime CreatedUtc,
        int MessageSequence,
        int ToolCallId,
        ChatRole Role,
        string Content,
        bool IsTool);

    private sealed record ContinuationTarget(
        AgentMessage? SourceUser,
        AgentMessage? Assistant,
        IReadOnlyList<string> ToolSummaries);
}
