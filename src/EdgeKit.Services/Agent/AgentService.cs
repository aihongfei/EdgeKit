using System.ClientModel;
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ISettingsService _settings;
    private readonly IAgentRepository _repository;
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AgentToolExecutor _toolExecutor;

    private long _activeConversationId;
    private long _activeAssistantMessageId;
    private int _toolCallsThisTurn;

    public AgentService(
        ISettingsService settings,
        IAgentRepository repository,
        AgentToolRegistry toolRegistry,
        AgentToolExecutor toolExecutor)
    {
        _settings = settings;
        _repository = repository;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;

        _repository.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public AgentSettings GetSettings()
    {
        var apiKey = string.Empty;
        try
        {
            apiKey = SecretProtector.Unprotect(_settings.AiApiKeyEncrypted);
        }
        catch
        {
            apiKey = string.Empty;
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
            _settings.AiAllowClipboardTools);
    }

    public void SaveSettings(AgentSettings settings)
    {
        _settings.AiEnabled = settings.Enabled;
        _settings.AiBaseUrl = settings.BaseUrl;
        _settings.AiModel = settings.Model;
        _settings.AiTemperature = settings.Temperature;
        _settings.AiDefaultMode = settings.DefaultMode;
        _settings.AiActionMode = settings.ActionMode;
        _settings.AiAllowClipboardTools = settings.AllowClipboardTools;

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _settings.AiApiKeyEncrypted = SecretProtector.Protect(settings.ApiKey.Trim());
            _settings.AiApiKeyPreview = SecretProtector.BuildPreview(settings.ApiKey.Trim());
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
            var agent = BuildAgent(settings, AgentConversationMode.Chat);
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

    public AgentConversation CreateConversation(AgentConversationMode mode)
    {
        var settings = GetSettings();
        var title = mode switch
        {
            AgentConversationMode.Translate => "翻译会话",
            AgentConversationMode.WindowsConfig => "Windows 配置",
            _ => "新对话"
        };

        return _repository.CreateConversation(title, mode, settings.Model);
    }

    public void UpdateConversation(long id, string title, AgentConversationMode mode)
    {
        var settings = GetSettings();
        _repository.UpdateConversation(id, string.IsNullOrWhiteSpace(title) ? "未命名会话" : title.Trim(), mode, settings.Model);
    }

    public void ArchiveConversation(long id, bool archived)
        => _repository.ArchiveConversation(id, archived);

    public void DeleteConversation(long id)
        => _repository.DeleteConversation(id);

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
        _activeConversationId = conversationId;
        _activeAssistantMessageId = 0;
        _toolCallsThisTurn = 0;

        try
        {
            var agent = BuildAgent(settings, detail.Conversation.Mode);
            var session = await CreateSessionAsync(agent, detail.Conversation, detail.Messages, cancellationToken).ConfigureAwait(false);
            var response = await agent.RunAsync(
                message.Trim(),
                session,
                BuildRunOptions(settings, detail.Conversation.Mode),
                cancellationToken).ConfigureAwait(false);

            var assistantText = string.IsNullOrWhiteSpace(response.Text)
                ? "(模型没有返回文本内容)"
                : response.Text.Trim();
            var assistantMessage = _repository.AddMessage(conversationId, AgentMessageRole.Assistant, assistantText);
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);

            var pendingTools = _repository.GetPendingToolCalls(conversationId);
            return new AgentSendResult(true, userMessage, assistantMessage, pendingTools, string.Empty);
        }
        catch (Exception ex) when (IsAgentCallException(ex))
        {
            var failed = _repository.AddMessage(conversationId, AgentMessageRole.Assistant, string.Empty, AgentMessageStatus.Failed, ex.Message);
            return new AgentSendResult(false, userMessage, failed, _repository.GetPendingToolCalls(conversationId), ex.Message);
        }
        finally
        {
            _activeConversationId = 0;
            _activeAssistantMessageId = 0;
            _toolCallsThisTurn = 0;
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
        var lastToolSignature = string.Empty;

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
            assistantMessage = _repository.AddMessage(conversationId, AgentMessageRole.Assistant, "正在思考...", AgentMessageStatus.Pending);
            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.Started, userMessage, assistantMessage, string.Empty, GetToolCalls(conversationId), string.Empty),
                cancellationToken).ConfigureAwait(false);

            _activeConversationId = conversationId;
            _activeAssistantMessageId = assistantMessage.Id;
            _toolCallsThisTurn = 0;

            var agent = BuildAgent(settings, detail.Conversation.Mode);
            var session = await CreateSessionAsync(agent, detail.Conversation, detail.Messages, cancellationToken).ConfigureAwait(false);
            var responseText = new StringBuilder();

            await foreach (var update in agent.RunStreamingAsync(
                    trimmedMessage,
                    session,
                    BuildRunOptions(settings, detail.Conversation.Mode),
                    cancellationToken).ConfigureAwait(false))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    responseText.Append(delta);
                    await writer.WriteAsync(
                        new AgentStreamEvent(AgentStreamEventKind.Delta, userMessage, assistantMessage, delta, GetToolCalls(conversationId), string.Empty),
                        cancellationToken).ConfigureAwait(false);
                }

                lastToolSignature = await WriteToolChangesIfNeededAsync(
                    writer,
                    conversationId,
                    userMessage,
                    assistantMessage,
                    lastToolSignature,
                    cancellationToken).ConfigureAwait(false);
            }

            var assistantText = responseText.Length == 0
                ? "(模型没有返回文本内容)"
                : responseText.ToString().Trim();
            _repository.UpdateMessage(assistantMessage.Id, assistantText, AgentMessageStatus.Complete);
            var completedAssistant = assistantMessage with
            {
                Content = assistantText,
                Status = AgentMessageStatus.Complete,
                Error = string.Empty
            };
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);

            await writer.WriteAsync(
                new AgentStreamEvent(AgentStreamEventKind.Completed, userMessage, completedAssistant, string.Empty, GetToolCalls(conversationId), string.Empty),
                cancellationToken).ConfigureAwait(false);
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
                    Error = ex.Message
                };
            }

            await writer.WriteAsync(FailedStreamEvent(userMessage, assistantMessage, conversationId, ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _activeConversationId = 0;
            _activeAssistantMessageId = 0;
            _toolCallsThisTurn = 0;
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
            var followUp = await SendToolFollowUpAsync(call.ConversationId, updated, cancellationToken).ConfigureAwait(false);
            assistantMessage = followUp.AssistantMessage;
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
        return new AgentToolApprovalResult(true, updated, message, null, "已拒绝");
    }

    private async Task<AgentSendResult> SendToolFollowUpAsync(
        long conversationId,
        AgentToolCall call,
        CancellationToken cancellationToken)
    {
        var detail = _repository.GetConversation(conversationId);
        var settings = GetSettings();
        if (detail is null || ValidateSettings(settings) is not null)
        {
            return new AgentSendResult(false, null, null, Array.Empty<AgentToolCall>(), string.Empty);
        }

        try
        {
            var agent = BuildAgent(settings, detail.Conversation.Mode);
            var session = await CreateSessionAsync(agent, detail.Conversation, detail.Messages, cancellationToken).ConfigureAwait(false);
            var prompt = $"工具 {call.ToolName} 已执行完成，请根据以下结果用中文简洁总结，并说明下一步建议：\n{call.ResultSummary}";
            var response = await agent.RunAsync(
                prompt,
                session,
                BuildRunOptions(settings, detail.Conversation.Mode),
                cancellationToken).ConfigureAwait(false);
            var assistantMessage = _repository.AddMessage(
                conversationId,
                AgentMessageRole.Assistant,
                string.IsNullOrWhiteSpace(response.Text) ? "工具已执行完成。" : response.Text.Trim());
            await SaveSessionAsync(agent, session, conversationId, cancellationToken).ConfigureAwait(false);
            return new AgentSendResult(true, null, assistantMessage, _repository.GetPendingToolCalls(conversationId), string.Empty);
        }
        catch (Exception ex) when (IsAgentCallException(ex))
        {
            return new AgentSendResult(false, null, null, _repository.GetPendingToolCalls(conversationId), ex.Message);
        }
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

    private ChatClientAgent BuildAgent(AgentSettings settings, AgentConversationMode mode)
    {
        var chatClient = CreateChatClient(settings);
        var tools = BuildTools(mode, settings);
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
                AllowMultipleToolCalls = true,
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
            .UseFunctionInvocation()
            .Build(null);
    }

    private IList<AIFunction> BuildTools(AgentConversationMode mode, AgentSettings settings)
    {
        var descriptors = _toolRegistry.GetTools(mode, settings)
            .Where(d => _toolExecutor.ShouldExposeTool(d, settings))
            .ToArray();
        var result = new List<AIFunction>();

        foreach (var descriptor in descriptors)
        {
            result.Add(AIFunctionFactory.Create(
                (Func<JsonElement, CancellationToken, Task<string>>)((arguments, cancellationToken) => InvokeToolAsync(descriptor.Id, arguments, cancellationToken)),
                new AIFunctionFactoryOptions
                {
                    Name = descriptor.Id,
                    Description = descriptor.Description
                }));
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
        if (descriptor is null)
        {
            return "未知工具: " + toolId;
        }

        var argsJson = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        var argsSummary = AgentToolExecutor.ArgumentsSummary(arguments);
        var approval = _toolExecutor.CanAutoExecute(descriptor, settings)
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

        if (approval == AgentToolApprovalStatus.Pending)
        {
            return $"工具“{descriptor.Name}”需要用户确认后执行。调用编号: {call.Id}";
        }

        var result = await _toolExecutor.ExecuteAsync(toolId, arguments, cancellationToken).ConfigureAwait(false);
        _repository.UpdateToolCall(
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
        return result.Success ? result.Result : "工具执行失败: " + result.Error;
    }

    private ChatClientAgentRunOptions BuildRunOptions(AgentSettings settings, AgentConversationMode mode)
        => new(new ChatOptions
        {
            ModelId = settings.Model,
            Temperature = (float)settings.Temperature,
            Instructions = BuildInstructions(mode, settings),
            AllowMultipleToolCalls = true,
            ToolMode = new AutoChatToolMode()
        });

    private async Task<AgentSession> CreateSessionAsync(
        ChatClientAgent agent,
        AgentConversation conversation,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(conversation.AgentSessionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(conversation.AgentSessionJson);
                return await agent.DeserializeSessionAsync(document.RootElement, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Fall back to a fresh session populated below.
            }
        }

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var history = messages
            .Where(m => m.Status == AgentMessageStatus.Complete)
            .TakeLast(30)
            .Select(m => new Microsoft.Extensions.AI.ChatMessage(ToChatRole(m.Role), m.Content))
            .ToList();
        session.SetInMemoryChatHistory(history, null, JsonOptions);
        return session;
    }

    private async Task SaveSessionAsync(
        ChatClientAgent agent,
        AgentSession session,
        long conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await agent.SerializeSessionAsync(session, JsonOptions, cancellationToken).ConfigureAwait(false);
            _repository.SaveSession(conversationId, json.GetRawText());
        }
        catch
        {
            // Session persistence is best effort; messages are still stored.
        }
    }

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

    private static bool IsAgentCallException(Exception exception)
        => exception is HttpRequestException
            or TaskCanceledException
            or InvalidOperationException
            or ClientResultException
            or ArgumentException
            or NotSupportedException
            or JsonException;

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
            AgentActionMode.AutoWithWhitelist => "动作模式：白名单自动化；但危险动作仍必须确认。",
            _ => "动作模式：执行写操作前必须确认。"
        };

        return
            "你是 EdgeKit 桌面智能体，用中文简洁回答。" + Environment.NewLine +
            modeText + Environment.NewLine +
            actionText + Environment.NewLine +
            "不要声称已经执行未经过工具结果确认的操作。不要要求用户运行任意脚本，除非是在解释手动步骤。";
    }
}
