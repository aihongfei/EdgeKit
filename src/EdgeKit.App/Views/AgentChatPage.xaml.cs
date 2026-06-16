using System.Collections.ObjectModel;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Agent;
using EdgeKit.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.System;

namespace EdgeKit.App.Views;

public sealed partial class AgentChatPage : Page
{
    private const int StatusAutoCloseDelayMs = 2500;
    private static readonly Brush StopButtonBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 196, 43, 43));
    private static readonly Brush StopButtonHoverBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 220, 62, 62));
    private static readonly Brush StopButtonPressedBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 160, 28, 28));

    private readonly ObservableCollection<AgentTimelineItemViewModel> _timeline = new();
    private readonly Dictionary<long, AgentTimelineItemViewModel> _messageIndex = new();
    private readonly Dictionary<long, AgentTimelineItemViewModel> _toolCallIndex = new();
    private readonly HashSet<long> _toolActionsInProgress = new();
    private readonly HashSet<long> _titleGeneratingConversationIds = new();
    private readonly Queue<AgentStreamEvent> _pendingStreamEvents = new();

    private IAgentService? _agent;
    private long _selectedConversationId;
    private bool _loading;
    private bool _settingsLoading;
    private bool _sending;
    private bool _conversationDeleteDialogOpen;
    private bool _streamFlushScheduled;
    private AgentTimelineItemViewModel? _streamingAssistantItem;
    private AgentTimelineItemViewModel? _preferredScrollItem;
    private long _activeTurnAssistantMessageId;
    private AgentContextStatus? _contextStatus;
    private readonly HashSet<long> _scrolledToolCallIdsThisTurn = new();
    private CancellationTokenSource? _sendCancellation;
    private CancellationTokenSource? _statusAutoCloseCts;
    private ScrollViewer? _messageScrollViewer;
    private bool _scrollUpdateScheduled;

    public AgentChatPage()
    {
        InitializeComponent();
        _timeline.CollectionChanged += OnTimelineCollectionChanged;
        MessageList.ItemsSource = _timeline;
        PromptBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPromptKeyDown), handledEventsToo: true);
    }

    private void OnTimelineCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                _messageIndex.Clear();
                _toolCallIndex.Clear();
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                {
                    foreach (var old in e.OldItems.OfType<AgentTimelineItemViewModel>())
                    {
                        UnindexTimelineItem(old);
                    }
                }

                if (e.NewItems is not null)
                {
                    foreach (var added in e.NewItems.OfType<AgentTimelineItemViewModel>())
                    {
                        IndexTimelineItem(added);
                    }
                }

                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                {
                    foreach (var old in e.OldItems.OfType<AgentTimelineItemViewModel>())
                    {
                        UnindexTimelineItem(old);
                    }
                }

                break;
        }
    }

    private void IndexTimelineItem(AgentTimelineItemViewModel item)
    {
        if (item.Kind == AgentTimelineItemKind.Message && item.MessageId != 0)
        {
            _messageIndex[item.MessageId] = item;
        }
        else if (item.Kind == AgentTimelineItemKind.ToolCall && item.ToolCallId != 0)
        {
            _toolCallIndex[item.ToolCallId] = item;
        }
    }

    private void UnindexTimelineItem(AgentTimelineItemViewModel item)
    {
        if (item.Kind == AgentTimelineItemKind.Message && item.MessageId != 0)
        {
            _messageIndex.Remove(item.MessageId);
        }
        else if (item.Kind == AgentTimelineItemKind.ToolCall && item.ToolCallId != 0)
        {
            _toolCallIndex.Remove(item.ToolCallId);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AgentChatPageParameter parameter)
        {
            _agent = parameter.AgentService;
            _agent.Changed += OnAgentChanged;
            LoadAgentSettings();
            LoadConversations();
            LoadConversationDetail();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _sendCancellation?.Cancel();
        _sendCancellation?.Dispose();
        _sendCancellation = null;
        if (_agent is not null)
        {
            _agent.Changed -= OnAgentChanged;
        }

        _statusAutoCloseCts?.Cancel();
    }

    private void OnAgentChanged(object? sender, EventArgs e)
    {
        if (_sending || _toolActionsInProgress.Count > 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            LoadAgentSettings();
            LoadConversations(keepSelection: true);
            RefreshConversationDetail();
        });
    }

    private void LoadConversations(bool keepSelection = false)
    {
        if (_agent is null)
        {
            return;
        }

        _loading = true;
        try
        {
            var selected = keepSelection ? _selectedConversationId : 0;
            var items = _agent.GetConversations()
                .Select(c => new AgentConversationViewModel(c, _titleGeneratingConversationIds.Contains(c.Id)))
                .ToList();

            ConversationList.ItemsSource = items;
            var target = selected == 0 ? items.FirstOrDefault() : items.FirstOrDefault(i => i.Id == selected);
            if (target is not null)
            {
                ConversationList.SelectedItem = target;
                _selectedConversationId = target.Id;
            }
            else
            {
                _selectedConversationId = 0;
                _timeline.Clear();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadConversationDetail()
    {
        if (_agent is null || _selectedConversationId <= 0)
        {
            return;
        }

        var detail = _agent.GetConversation(_selectedConversationId);
        if (detail is null)
        {
            return;
        }

        _preferredScrollItem = null;
        _activeTurnAssistantMessageId = 0;
        _scrolledToolCallIdsThisTurn.Clear();
        RebuildTimeline(detail);
        UpdateContextStatus(_agent.GetContextStatus(_selectedConversationId));
        ScrollToPreferredItemOrEnd(force: true);
    }

    private void RefreshConversationDetail()
    {
        if (_agent is null || _selectedConversationId <= 0)
        {
            return;
        }

        var detail = _agent.GetConversation(_selectedConversationId);
        if (detail is null)
        {
            return;
        }

        foreach (var message in detail.Messages.Where(m => m.Role != AgentMessageRole.Tool).OrderBy(m => m.CreatedUtc).ThenBy(m => m.Sequence))
        {
            UpsertMessage(message);
        }

        UpsertToolCalls(detail.ToolCalls);
        UpdateContextStatus(_agent.GetContextStatus(_selectedConversationId));
        if (_sending)
        {
            return;
        }

        ScrollToPreferredItemOrEnd();
    }

    private void RebuildTimeline(AgentConversationDetail detail)
    {
        _timeline.Clear();

        var orphanTools = detail.ToolCalls
            .Where(t => t.MessageId is null or 0)
            .OrderBy(t => t.CreatedUtc)
            .ToList();

        foreach (var orphan in orphanTools)
        {
            var item = new AgentTimelineItemViewModel(orphan);
            _timeline.Add(item);
            RememberInitialScrollTool(item);
        }

        foreach (var message in detail.Messages.OrderBy(m => m.CreatedUtc).ThenBy(m => m.Sequence))
        {
            if (message.Role == AgentMessageRole.Tool)
            {
                continue;
            }

            var relatedTools = detail.ToolCalls
                .Where(t => t.MessageId == message.Id)
                .OrderBy(t => t.CreatedUtc);

            foreach (var tool in relatedTools)
            {
                var item = new AgentTimelineItemViewModel(tool);
                _timeline.Add(item);
                RememberInitialScrollTool(item);
            }

            _timeline.Add(new AgentTimelineItemViewModel(message));
        }
    }

    private void OnConversationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sending || _loading || ConversationList.SelectedItem is not AgentConversationViewModel item)
        {
            return;
        }

        _selectedConversationId = item.Id;
        LoadConversationDetail();
    }

    private void OnNewConversationClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || _sending)
        {
            return;
        }

        var conversation = _agent.CreateConversation(AgentConversationMode.Chat);
        _selectedConversationId = conversation.Id;
        LoadConversations(keepSelection: true);
        LoadConversationDetail();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void OnDeleteConversationClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || _sending || _conversationDeleteDialogOpen || (sender as FrameworkElement)?.Tag is not long id)
        {
            return;
        }

        var button = sender as Button;
        _conversationDeleteDialogOpen = true;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            if (ConversationList.ItemsSource is IEnumerable<AgentConversationViewModel> items
                && items.FirstOrDefault(i => i.Id == id) is { } item)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "删除会话",
                    Content = $"确定删除「{item.Title}」？此操作会删除会话记录和工具调用记录。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            _agent.DeleteConversation(id);
            if (_selectedConversationId == id)
            {
                _selectedConversationId = 0;
                _timeline.Clear();
            }

            LoadConversations();
            LoadConversationDetail();
            PromptBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent conversation delete failed");
            ShowStatus("删除失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _conversationDeleteDialogOpen = false;
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private void OnConversationDeletePointerPressed(object sender, PointerRoutedEventArgs e)
        => e.Handled = true;

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            CancelCurrentSend();
            return;
        }

        await RunSafelyAsync(SendCurrentPromptAsync);
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Accept))
        {
            return;
        }

        if (IsShiftDown() || IsAltDown())
        {
            e.Handled = true;
            DispatcherQueue.TryEnqueue(InsertPromptNewLine);
            return;
        }

        e.Handled = true;
        if (_sending)
        {
            return;
        }

        await RunSafelyAsync(SendCurrentPromptAsync);
    }

    private async Task SendCurrentPromptAsync()
    {
        if (_agent is null || _sending)
        {
            return;
        }

        if (_selectedConversationId <= 0)
        {
            var conversation = _agent.CreateConversation(AgentConversationMode.Chat);
            _selectedConversationId = conversation.Id;
            LoadConversations(keepSelection: true);
        }

        var text = PromptBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var conversationId = _selectedConversationId;
        var shouldGenerateTitle = ShouldGenerateTitle(conversationId);
        PromptBox.Text = string.Empty;
        BeginNewTurnUiState();
        SetBusy(true);
        PromptBox.Focus(FocusState.Programmatic);
        _streamingAssistantItem = null;
        _sendCancellation?.Dispose();
        _sendCancellation = new CancellationTokenSource();
        var token = _sendCancellation.Token;

        try
        {
            await foreach (var item in _agent.SendStreamingAsync(conversationId, text, token))
            {
                EnqueueStreamEvent(item);
            }

            await FlushPendingStreamEventsAsync(force: true);

            if (shouldGenerateTitle)
            {
                ScheduleConversationTitleGeneration(conversationId, text);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await FlushPendingStreamEventsAsync(force: true);
            ShowStatus("已中止", "已中止本次回复。", InfoBarSeverity.Informational);
        }
        finally
        {
            _streamingAssistantItem = null;
            _pendingStreamEvents.Clear();
            _streamFlushScheduled = false;
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            SetBusy(false);
            PromptBox.Focus(FocusState.Programmatic);
            LoadConversations(keepSelection: true);
            RefreshConversationDetail();
        }
    }

    private void CancelCurrentSend()
    {
        if (_sendCancellation?.IsCancellationRequested == true)
        {
            return;
        }

        _sendCancellation?.Cancel();
        ShowStatus("正在中止", "正在停止当前回复...", InfoBarSeverity.Informational);
    }

    private void BeginNewTurnUiState()
    {
        _preferredScrollItem = null;
        _activeTurnAssistantMessageId = 0;
        _scrolledToolCallIdsThisTurn.Clear();
        foreach (var tool in _timeline.Where(i => i.ToolCall?.ExecutionStatus == AgentToolExecutionStatus.Failed))
        {
            tool.CollapseFailedToolCallForNewTurn();
        }
    }

    private void EnqueueStreamEvent(AgentStreamEvent item)
    {
        _pendingStreamEvents.Enqueue(item);
        if (_streamFlushScheduled)
        {
            return;
        }

        _streamFlushScheduled = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _streamFlushScheduled = false;
                FlushPendingStreamEvents();
            });
    }

    private Task FlushPendingStreamEventsAsync(bool force = false)
        => DispatcherQueue.EnqueueAsync(() =>
        {
            if (force)
            {
                _streamFlushScheduled = false;
            }

            FlushPendingStreamEvents();
        });

    private void FlushPendingStreamEvents()
    {
        if (_pendingStreamEvents.Count == 0)
        {
            return;
        }

        while (_pendingStreamEvents.Count > 0)
        {
            ApplyStreamEvent(_pendingStreamEvents.Dequeue(), scroll: false);
        }

        ScrollToPreferredItemOrEnd(force: true);
        ClearConsumedScrollTarget();
    }

    private void ApplyStreamEvent(AgentStreamEvent item, bool scroll = true)
    {
        if (item.UserMessage is not null)
        {
            UpsertMessage(item.UserMessage);
        }

        if (item.AssistantMessage is not null)
        {
            if (item.Kind == AgentStreamEventKind.ToolCallsChanged && _streamingAssistantItem is not null)
            {
                _streamingAssistantItem.UpdateMessageState(item.AssistantMessage);
            }
            else if (item.Kind is AgentStreamEventKind.Completed or AgentStreamEventKind.Failed or AgentStreamEventKind.PausedForToolApproval
                     || _streamingAssistantItem is null)
            {
                _streamingAssistantItem = UpsertMessage(item.AssistantMessage);
            }

            _streamingAssistantItem.IsStreaming = item.Kind is AgentStreamEventKind.Started
                or AgentStreamEventKind.Delta
                or AgentStreamEventKind.ToolCallsChanged;
            if (item.Kind == AgentStreamEventKind.Started)
            {
                _streamingAssistantItem.StreamedContent = string.Empty;
                _activeTurnAssistantMessageId = item.AssistantMessage.Id;
            }
        }

        if (item.Kind == AgentStreamEventKind.Delta)
        {
            _streamingAssistantItem?.AppendDelta(item.Delta);
        }

        UpsertToolCalls(item.ToolCalls);
        if (item.ContextStatus is not null)
        {
            UpdateContextStatus(item.ContextStatus);
        }

        if (item.Kind == AgentStreamEventKind.Completed && item.AssistantMessage is not null)
        {
            _streamingAssistantItem?.UpdateMessage(item.AssistantMessage);
            _streamingAssistantItem = null;
        }

        if (item.Kind == AgentStreamEventKind.PausedForToolApproval && item.AssistantMessage is not null)
        {
            _streamingAssistantItem?.UpdateMessage(item.AssistantMessage);
            _streamingAssistantItem = null;
        }

        if (item.Kind == AgentStreamEventKind.Failed)
        {
            if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            {
                ShowStatus("发送失败", item.ErrorMessage, InfoBarSeverity.Error);
            }

            if (item.AssistantMessage is not null)
            {
                UpsertMessage(item.AssistantMessage);
            }

            _streamingAssistantItem = null;
        }

        if (scroll)
        {
            ScrollToPreferredItemOrEnd(force: true);
            ClearConsumedScrollTarget();
        }
    }

    private AgentTimelineItemViewModel UpsertMessage(AgentMessage message)
    {
        if (_messageIndex.TryGetValue(message.Id, out var existing))
        {
            existing.UpdateMessage(message);
            CollapseCompletedToolsForMessage(message);
            return existing;
        }

        var item = new AgentTimelineItemViewModel(message)
        {
            IsStreaming = false,
            StreamedContent = message.Content
        };
        _timeline.Add(item);
        CollapseCompletedToolsForMessage(message);
        return item;
    }

    private void CollapseCompletedToolsForMessage(AgentMessage message)
    {
        if (message.Role != AgentMessageRole.Assistant || message.Status != AgentMessageStatus.Complete)
        {
            return;
        }

        foreach (var tool in _timeline.Where(i => i.ToolCall?.MessageId == message.Id))
        {
            tool.CollapseCompletedToolCall();
        }
    }

    private void UpsertToolCalls(IReadOnlyList<AgentToolCall> calls)
    {
        foreach (var call in calls)
        {
            if (_toolCallIndex.TryGetValue(call.Id, out var existing))
            {
                existing.UpdateToolCall(call);
                RememberPreferredScrollTool(existing);
                continue;
            }

            var item = new AgentTimelineItemViewModel(call);
            RememberPreferredScrollTool(item);
            var relatedMessage = call.MessageId is long messageId && _messageIndex.TryGetValue(messageId, out var related)
                ? related
                : null;
            var messageIndex = relatedMessage is null ? -1 : _timeline.IndexOf(relatedMessage);
            var assistantIndex = messageIndex >= 0
                ? messageIndex
                : _streamingAssistantItem is not null
                ? _timeline.IndexOf(_streamingAssistantItem)
                : -1;

            if (assistantIndex >= 0)
            {
                _timeline.Insert(assistantIndex, item);
            }
            else
            {
                _timeline.Add(item);
            }
        }
    }

    private void RememberPreferredScrollTool(AgentTimelineItemViewModel item)
    {
        if (item.ToolCall is null
            || _activeTurnAssistantMessageId <= 0
            || item.ToolCall.MessageId != _activeTurnAssistantMessageId)
        {
            return;
        }

        if (item.ToolCall.ApprovalStatus == AgentToolApprovalStatus.Pending
            || item.ToolCall.ExecutionStatus is AgentToolExecutionStatus.Pending or AgentToolExecutionStatus.Running)
        {
            _preferredScrollItem = item;
            return;
        }

        if (item.ToolCall.ExecutionStatus == AgentToolExecutionStatus.Failed
            && _scrolledToolCallIdsThisTurn.Add(item.ToolCall.Id))
        {
            _preferredScrollItem = item;
            return;
        }

        if (ReferenceEquals(_preferredScrollItem, item))
        {
            _preferredScrollItem = null;
        }
    }

    private void RememberInitialScrollTool(AgentTimelineItemViewModel item)
    {
        if (item.ToolCall?.ApprovalStatus == AgentToolApprovalStatus.Pending
            || item.ToolCall?.ExecutionStatus is AgentToolExecutionStatus.Pending or AgentToolExecutionStatus.Running)
        {
            _preferredScrollItem = item;
        }
    }

    private void ClearConsumedScrollTarget()
    {
        if (_preferredScrollItem?.ToolCall?.ExecutionStatus == AgentToolExecutionStatus.Failed)
        {
            _preferredScrollItem = null;
        }
    }

    private async void OnApproveToolClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || (sender as FrameworkElement)?.Tag is not long id)
        {
            return;
        }

        if (!_toolActionsInProgress.Add(id))
        {
            return;
        }

        var button = sender as Button;
        try
        {
            if (button is not null)
            {
                button.IsEnabled = false;
            }

            MarkToolCallRunning(id);
            var result = await _agent.ApproveToolCallAsync(id);
            if (result.ToolCall is not null)
            {
                UpsertToolCalls(new[] { result.ToolCall });
            }

            if (result.AssistantMessage is not null)
            {
                UpsertMessage(result.AssistantMessage);
            }

            ShowStatus(result.Success ? "工具已执行" : "工具执行失败", result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            LoadConversations(keepSelection: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent tool approval failed unexpectedly");
            ShowStatus("工具执行失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _toolActionsInProgress.Remove(id);
            if (button is not null)
            {
                button.IsEnabled = true;
            }

            RefreshConversationDetail();
        }
    }

    private void OnToolActionPointerPressed(object sender, PointerRoutedEventArgs e)
        => e.Handled = true;

    private void OnRejectToolClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || (sender as FrameworkElement)?.Tag is not long id)
        {
            return;
        }

        var result = _agent.RejectToolCall(id);
        if (result.ToolCall is not null)
        {
            UpsertToolCalls(new[] { result.ToolCall });
        }

        if (result.AssistantMessage is not null)
        {
            UpsertMessage(result.AssistantMessage);
        }

        ShowStatus(result.Success ? "已拒绝" : "拒绝失败", result.Message, result.Success ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
    }

    private void ScheduleConversationTitleGeneration(long conversationId, string firstUserMessage)
    {
        if (_agent is null)
        {
            return;
        }

        var agent = _agent;
        if (!_titleGeneratingConversationIds.Add(conversationId))
        {
            return;
        }

        LoadConversations(keepSelection: true);
        _ = Task.Run(async () =>
        {
            try
            {
                await agent.TryGenerateConversationTitleAsync(conversationId, firstUserMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Agent conversation title generation failed");
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _titleGeneratingConversationIds.Remove(conversationId);
                    LoadConversations(keepSelection: true);
                });
            }
        });
    }

    private void MarkToolCallRunning(long id)
    {
        if (!_toolCallIndex.TryGetValue(id, out var existing) || existing.ToolCall is null)
        {
            return;
        }

        existing.UpdateToolCall(existing.ToolCall with
        {
            ApprovalStatus = AgentToolApprovalStatus.Approved,
            ExecutionStatus = AgentToolExecutionStatus.Running,
            ResultSummary = string.Empty,
            Error = string.Empty
        });
    }

    private void OnShowSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadAgentSettings();
        ChatPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void OnShowChatClick(object sender, RoutedEventArgs e)
    {
        SaveAgentSettings(includeApiKey: false, includeSearchApiKey: false);
        SettingsPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
    }

    private void LoadAgentSettings()
    {
        if (_agent is null)
        {
            return;
        }

        var settings = _agent.GetSettings();
        _settingsLoading = true;
        AiEnabledSwitch.IsOn = settings.Enabled;
        AiBaseUrlBox.Text = settings.BaseUrl;
        AiModelBox.Text = settings.Model;
        AiApiKeyBox.Password = string.Empty;
        AiApiKeyHintText.Text = string.IsNullOrWhiteSpace(settings.ApiKeyPreview)
            ? "未配置 API Key"
            : "已配置 API Key " + settings.ApiKeyPreview;
        AiTemperatureBox.Value = settings.Temperature;
        AiContextWindowTokensBox.Value = settings.ContextWindowTokens;
        SelectActionMode(settings.ActionMode);
        AiAllowClipboardToolsSwitch.IsOn = settings.AllowClipboardTools;
        AiEnableFileToolsSwitch.IsOn = settings.EnableFileTools;
        AiEnableShellToolsSwitch.IsOn = settings.EnableShellTools;
        AiEnableWebToolsSwitch.IsOn = settings.EnableWebTools;
        AiEnableMcpToolsSwitch.IsOn = settings.EnableMcpTools;
        SelectSearchProvider(settings.SearchProvider);
        AiSearchApiKeyBox.Password = string.Empty;
        AiSearchApiKeyHintText.Text = string.IsNullOrWhiteSpace(settings.SearchApiKeyPreview)
            ? "未配置 Search API Key"
            : "已配置 Search API Key " + settings.SearchApiKeyPreview;
        AiTrustedDirectoriesBox.Text = settings.TrustedDirectories;
        AiShellCommandWhitelistBox.Text = settings.ShellCommandWhitelist;
        AiMcpServersJsonBox.Text = settings.McpServersJson;
        _settingsLoading = false;
    }

    private void OnAgentSettingChanged(object sender, RoutedEventArgs e)
        => SaveAgentSettings(includeApiKey: false, includeSearchApiKey: false);

    private void OnAgentSelectionSettingChanged(object sender, SelectionChangedEventArgs e)
        => SaveAgentSettings(includeApiKey: false, includeSearchApiKey: false);

    private void OnAgentTextSettingLostFocus(object sender, RoutedEventArgs e)
        => SaveAgentSettings(includeApiKey: false, includeSearchApiKey: false);

    private void OnAgentNumberSettingChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_settingsLoading || double.IsNaN(args.NewValue))
        {
            return;
        }

        SaveAgentSettings(includeApiKey: false, includeSearchApiKey: false);
    }

    private void OnAgentApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading || string.IsNullOrWhiteSpace(AiApiKeyBox.Password))
        {
            return;
        }

        SaveAgentSettings(includeApiKey: true, includeSearchApiKey: false);
    }

    private void OnAgentSearchApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading || string.IsNullOrWhiteSpace(AiSearchApiKeyBox.Password))
        {
            return;
        }

        SaveAgentSettings(includeApiKey: false, includeSearchApiKey: true);
    }

    private async void OnAgentTestClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            return;
        }

        SaveAgentSettings(
            includeApiKey: !string.IsNullOrWhiteSpace(AiApiKeyBox.Password),
            includeSearchApiKey: !string.IsNullOrWhiteSpace(AiSearchApiKeyBox.Password));
        AiTestButton.IsEnabled = false;
        AgentSettingsStatusBar.IsOpen = false;
        var result = await _agent.TestConnectionAsync();
        AiTestButton.IsEnabled = true;
        AgentSettingsStatusBar.Title = result.Success ? "连接成功" : "连接失败";
        AgentSettingsStatusBar.Message = result.Message;
        AgentSettingsStatusBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        AgentSettingsStatusBar.IsOpen = true;
    }

    private void SaveAgentSettings(bool includeApiKey, bool includeSearchApiKey)
    {
        if (_settingsLoading || _agent is null)
        {
            return;
        }

        var current = _agent.GetSettings();
        _agent.SaveSettings(new AgentSettings(
            AiEnabledSwitch.IsOn,
            AiBaseUrlBox.Text,
            AiModelBox.Text,
            includeApiKey ? AiApiKeyBox.Password : string.Empty,
            current.ApiKeyPreview,
            double.IsNaN(AiTemperatureBox.Value) ? current.Temperature : AiTemperatureBox.Value,
            AgentConversationMode.Chat,
            GetSelectedActionMode(),
            AiAllowClipboardToolsSwitch.IsOn,
            AiEnableFileToolsSwitch.IsOn,
            AiEnableShellToolsSwitch.IsOn,
            AiEnableWebToolsSwitch.IsOn,
            AiEnableMcpToolsSwitch.IsOn,
            GetSelectedSearchProvider(),
            includeSearchApiKey ? AiSearchApiKeyBox.Password : string.Empty,
            current.SearchApiKeyPreview,
            AiTrustedDirectoriesBox.Text,
            AiShellCommandWhitelistBox.Text,
            AiMcpServersJsonBox.Text,
            double.IsNaN(AiContextWindowTokensBox.Value) ? current.ContextWindowTokens : (int)AiContextWindowTokensBox.Value));

        if (includeApiKey || includeSearchApiKey)
        {
            _settingsLoading = true;
            var updated = _agent.GetSettings();
            if (includeApiKey)
            {
                AiApiKeyBox.Password = string.Empty;
                AiApiKeyHintText.Text = "已配置 API Key " + updated.ApiKeyPreview;
            }

            if (includeSearchApiKey)
            {
                AiSearchApiKeyBox.Password = string.Empty;
                AiSearchApiKeyHintText.Text = "已配置 Search API Key " + updated.SearchApiKeyPreview;
            }

            _settingsLoading = false;
        }
    }

    private AgentActionMode GetSelectedActionMode()
    {
        var tag = (AiActionModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "SuggestOnly" => AgentActionMode.SuggestOnly,
            "AutoWithWhitelist" => AgentActionMode.AutoWithWhitelist,
            _ => AgentActionMode.ConfirmBeforeAction
        };
    }

    private AgentSearchProvider GetSelectedSearchProvider()
    {
        var tag = (AiSearchProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag == "Tavily" ? AgentSearchProvider.Tavily : AgentSearchProvider.Brave;
    }

    private void SelectActionMode(AgentActionMode mode)
    {
        var tag = mode switch
        {
            AgentActionMode.SuggestOnly => "SuggestOnly",
            AgentActionMode.AutoWithWhitelist => "AutoWithWhitelist",
            _ => "ConfirmBeforeAction"
        };

        SelectComboTag(AiActionModeBox, tag);
    }

    private void SelectSearchProvider(AgentSearchProvider provider)
        => SelectComboTag(AiSearchProviderBox, provider == AgentSearchProvider.Tavily ? "Tavily" : "Brave");

    private static void SelectComboTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _sending = busy;
        ConversationList.IsEnabled = !busy;
        SendIcon.Glyph = busy ? "\uE769" : "\uE724";
        ToolTipService.SetToolTip(SendButton, busy ? "中止回复" : "发送");
        SendButton.Style = busy ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        SendButton.Background = busy ? StopButtonBrush : null;
        SendButton.Foreground = (Brush)Application.Current.Resources["EdgeTextBrush"];
        SendButton.BorderBrush = busy ? StopButtonHoverBrush : null;
        if (busy)
        {
            SendButton.Resources["ButtonBackgroundPointerOver"] = StopButtonHoverBrush;
            SendButton.Resources["ButtonBackgroundPressed"] = StopButtonPressedBrush;
        }
        else
        {
            SendButton.Resources.Remove("ButtonBackgroundPointerOver");
            SendButton.Resources.Remove("ButtonBackgroundPressed");
        }
    }

    private void UpdateContextStatus(AgentContextStatus? status)
    {
        _contextStatus = status;
        if (status is null)
        {
            ContextProgressRing.IsIndeterminate = false;
            ContextProgressRing.Value = 0;
            ContextPercentText.Text = "0%";
            ContextUsageBar.Value = 0;
            ContextSummaryText.Text = "暂无上下文信息。";
            ContextCompressingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var percent = Math.Clamp((int)Math.Round(status.UsageRatio * 100), 0, 999);
        var ringValue = Math.Clamp(percent, 0, 100);
        ContextProgressRing.IsIndeterminate = status.IsCompressing;
        if (!status.IsCompressing)
        {
            ContextProgressRing.Value = ringValue;
        }

        ContextPercentText.Text = percent.ToString() + "%";
        ContextUsageBar.Value = ringValue;
        ContextCompressingPanel.Visibility = status.IsCompressing ? Visibility.Visible : Visibility.Collapsed;
        ContextSummaryText.Text =
            $"约 {status.EstimatedTokens:N0} / {status.ContextWindowTokens:N0} tokens" + Environment.NewLine +
            $"消息: {status.MessageCount:N0}  工具摘要: {status.ToolSummaryCount:N0}  压缩摘要: {status.CompressionSummaryCount:N0}" +
            (status.LastCompressedUtc is null
                ? string.Empty
                : Environment.NewLine + "最近压缩: " + status.LastCompressedUtc.Value.ToLocalTime().ToString("MM-dd HH:mm"));
    }

    private void OnContextStatusClick(object sender, RoutedEventArgs e)
    {
        if (_agent is not null && _selectedConversationId > 0)
        {
            UpdateContextStatus(_agent.GetContextStatus(_selectedConversationId));
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        _statusAutoCloseCts?.Cancel();
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            ScheduleStatusAutoClose();
        }
    }

    private void ScheduleStatusAutoClose()
    {
        _statusAutoCloseCts = new CancellationTokenSource();
        var token = _statusAutoCloseCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StatusAutoCloseDelayMs, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() => StatusBar.IsOpen = false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private bool ShouldGenerateTitle(long conversationId)
    {
        var detail = _agent?.GetConversation(conversationId);
        return detail is not null && IsDefaultConversationTitle(detail.Conversation.Title);
    }

    private static bool IsDefaultConversationTitle(string title)
        => string.IsNullOrWhiteSpace(title)
            || string.Equals(title.Trim(), "新对话", StringComparison.Ordinal)
            || string.Equals(title.Trim(), "翻译会话", StringComparison.Ordinal)
            || string.Equals(title.Trim(), "Windows 配置", StringComparison.Ordinal);

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent page action failed unexpectedly");
            SetBusy(false);
            ShowStatus("操作失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void InsertPromptNewLine()
    {
        var start = PromptBox.SelectionStart;
        var length = PromptBox.SelectionLength;
        var text = PromptBox.Text ?? string.Empty;
        PromptBox.Text = text.Remove(start, length).Insert(start, Environment.NewLine);
        PromptBox.SelectionStart = start + Environment.NewLine.Length;
    }

    private static bool IsAltDown()
        => IsKeyDown(VirtualKey.Menu)
            || IsKeyDown(VirtualKey.LeftMenu)
            || IsKeyDown(VirtualKey.RightMenu);

    private static bool IsShiftDown()
        => IsKeyDown(VirtualKey.Shift)
            || IsKeyDown(VirtualKey.LeftShift)
            || IsKeyDown(VirtualKey.RightShift);

    private static bool IsKeyDown(VirtualKey key)
        => (NativeMethods.GetAsyncKeyState((int)key) & unchecked((short)0x8000)) != 0;

    private void ScrollMessagesToEnd(bool force = false)
    {
        ScrollMessagesToEndCore(force);
    }

    private void ScrollToPreferredItemOrEnd(bool force = false)
    {
        var target = _preferredScrollItem;
        if (target is not null && _timeline.Contains(target))
        {
            ScrollTimelineItemIntoView(target, force);
            return;
        }

        _preferredScrollItem = null;
        ScrollMessagesToEnd(force);
    }

    private void ScrollTimelineItemIntoView(AgentTimelineItemViewModel item, bool force)
        => ScrollTimelineItemIntoViewCore(item, force);

    private void ScrollTimelineItemIntoViewCore(AgentTimelineItemViewModel item, bool force)
    {
        var sv = GetMessageScrollViewer();
        if (sv is null)
        {
            return;
        }

        if (!force && sv.ScrollableHeight - sv.VerticalOffset > 80)
        {
            return;
        }

        MessageList.ScrollIntoView(item);
    }

    private void ScrollMessagesToEndCore(bool force)
    {
        var sv = GetMessageScrollViewer();
        if (sv is null)
        {
            return;
        }

        if (!force && sv.ScrollableHeight - sv.VerticalOffset > 80)
        {
            return;
        }

        if (_timeline.Count > 0)
        {
            MessageList.ScrollIntoView(_timeline[^1]);
        }

        // 用一次低优先级排队收敛布局，避免每个流式 delta 都触发同步 UpdateLayout 抖动。
        if (_scrollUpdateScheduled)
        {
            return;
        }

        _scrollUpdateScheduled = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _scrollUpdateScheduled = false;
                var inner = GetMessageScrollViewer();
                inner?.ChangeView(null, inner.ScrollableHeight, null, disableAnimation: true);
            });
    }

    private ScrollViewer? GetMessageScrollViewer()
    {
        if (_messageScrollViewer is not null)
        {
            return _messageScrollViewer;
        }

        _messageScrollViewer = FindScrollViewer(MessageList);
        return _messageScrollViewer;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            var result = FindScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue, Action action)
    {
        var completion = new TaskCompletionSource();
        if (!dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("无法切换到 UI 线程。"));
        }

        return completion.Task;
    }
}
