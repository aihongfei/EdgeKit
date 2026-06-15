using System.Collections.ObjectModel;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Agent;
using EdgeKit.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.System;

namespace EdgeKit.App.Views;

public sealed partial class AgentChatPage : Page
{
    private readonly ObservableCollection<AgentTimelineItemViewModel> _timeline = new();
    private readonly HashSet<long> _toolActionsInProgress = new();

    private IAgentService? _agent;
    private long _selectedConversationId;
    private bool _loading;
    private bool _settingsLoading;
    private bool _sending;
    private AgentTimelineItemViewModel? _streamingAssistantItem;

    public AgentChatPage()
    {
        InitializeComponent();
        MessageList.ItemsSource = _timeline;
        PromptBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPromptKeyDown), handledEventsToo: true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AgentChatPageParameter parameter)
        {
            _agent = parameter.AgentService;
            _agent.Changed += OnAgentChanged;
            SelectMode(_agent.GetSettings().DefaultMode);
            LoadAgentSettings();
            LoadConversations();
            LoadConversationDetail();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_agent is not null)
        {
            _agent.Changed -= OnAgentChanged;
        }
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
                .Select(c => new AgentConversationViewModel(c))
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

        RebuildTimeline(detail);
        SelectMode(detail.Conversation.Mode);
        ScrollMessagesToEnd(force: true);
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
        SelectMode(detail.Conversation.Mode);
        ScrollMessagesToEnd();
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
            _timeline.Add(new AgentTimelineItemViewModel(orphan));
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
                _timeline.Add(new AgentTimelineItemViewModel(tool));
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

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _agent is null || _selectedConversationId <= 0)
        {
            return;
        }

        var detail = _agent.GetConversation(_selectedConversationId);
        if (detail is null)
        {
            return;
        }

        _agent.UpdateConversation(_selectedConversationId, detail.Conversation.Title, GetSelectedMode());
        LoadConversations(keepSelection: true);
    }

    private void OnNewConversationClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || _sending)
        {
            return;
        }

        var conversation = _agent.CreateConversation(GetSelectedMode());
        _selectedConversationId = conversation.Id;
        LoadConversations(keepSelection: true);
        LoadConversationDetail();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(SendCurrentPromptAsync);
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (IsShiftDown() || IsAltDown())
        {
            e.Handled = true;
            InsertPromptNewLine();
            return;
        }

        e.Handled = true;
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
            var conversation = _agent.CreateConversation(GetSelectedMode());
            _selectedConversationId = conversation.Id;
            LoadConversations(keepSelection: true);
        }

        var text = PromptBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PromptBox.Text = string.Empty;
        SetBusy(true);
        PromptBox.Focus(FocusState.Programmatic);
        _streamingAssistantItem = null;

        try
        {
            await foreach (var item in _agent.SendStreamingAsync(_selectedConversationId, text))
            {
                await DispatcherQueue.EnqueueAsync(() => ApplyStreamEvent(item));
            }
        }
        finally
        {
            _streamingAssistantItem = null;
            SetBusy(false);
            PromptBox.Focus(FocusState.Programmatic);
            LoadConversations(keepSelection: true);
        }
    }

    private void ApplyStreamEvent(AgentStreamEvent item)
    {
        if (item.UserMessage is not null)
        {
            UpsertMessage(item.UserMessage);
        }

        if (item.AssistantMessage is not null)
        {
            if (item.Kind is AgentStreamEventKind.Completed or AgentStreamEventKind.Failed
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
            }
        }

        if (item.Kind == AgentStreamEventKind.Delta)
        {
            _streamingAssistantItem?.AppendDelta(item.Delta);
        }

        UpsertToolCalls(item.ToolCalls);

        if (item.Kind == AgentStreamEventKind.Completed && item.AssistantMessage is not null)
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

        ScrollMessagesToEnd(force: true);
    }

    private AgentTimelineItemViewModel UpsertMessage(AgentMessage message)
    {
        var existing = _timeline.FirstOrDefault(i => i.MessageId == message.Id);
        if (existing is not null)
        {
            existing.UpdateMessage(message);
            CollapseCompletedToolsForMessage(message);
            return existing;
        }

        var item = new AgentTimelineItemViewModel(message)
        {
            IsStreaming = message.Status == AgentMessageStatus.Pending,
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
            var existing = _timeline.FirstOrDefault(i => i.ToolCallId == call.Id);
            if (existing is not null)
            {
                existing.UpdateToolCall(call);
                continue;
            }

            var item = new AgentTimelineItemViewModel(call);
            var relatedMessage = call.MessageId is long messageId
                ? _timeline.FirstOrDefault(i => i.MessageId == messageId)
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

    private void MarkToolCallRunning(long id)
    {
        var existing = _timeline.FirstOrDefault(i => i.ToolCallId == id);
        if (existing?.ToolCall is null)
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
        SelectAgentMode(AiDefaultModeBox, settings.DefaultMode);
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
            GetSelectedAgentMode(AiDefaultModeBox),
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
            AiMcpServersJsonBox.Text));

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

    private AgentConversationMode GetSelectedMode()
    {
        var tag = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "Translate" => AgentConversationMode.Translate,
            "WindowsConfig" => AgentConversationMode.WindowsConfig,
            _ => AgentConversationMode.Chat
        };
    }

    private void SelectMode(AgentConversationMode mode)
    {
        _loading = true;
        var tag = mode switch
        {
            AgentConversationMode.Translate => "Translate",
            AgentConversationMode.WindowsConfig => "WindowsConfig",
            _ => "Chat"
        };

        foreach (var item in ModeBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                ModeBox.SelectedItem = item;
                break;
            }
        }

        _loading = false;
    }

    private static AgentConversationMode GetSelectedAgentMode(ComboBox box)
    {
        var tag = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "Translate" => AgentConversationMode.Translate,
            "WindowsConfig" => AgentConversationMode.WindowsConfig,
            _ => AgentConversationMode.Chat
        };
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

    private static void SelectAgentMode(ComboBox box, AgentConversationMode mode)
    {
        var tag = mode switch
        {
            AgentConversationMode.Translate => "Translate",
            AgentConversationMode.WindowsConfig => "WindowsConfig",
            _ => "Chat"
        };

        SelectComboTag(box, tag);
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
        SendButton.IsEnabled = !busy;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

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
        => (NativeMethods.GetKeyState((int)VirtualKey.Menu) & unchecked((short)0x8000)) != 0;

    private static bool IsShiftDown()
        => (NativeMethods.GetKeyState((int)VirtualKey.Shift) & unchecked((short)0x8000)) != 0;

    private void ScrollMessagesToEnd(bool force = false)
    {
        ScrollMessagesToEndCore(force);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ScrollMessagesToEndCore(force: true));
    }

    private void ScrollMessagesToEndCore(bool force)
    {
        var sv = FindScrollViewer(MessageList);
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

        MessageList.UpdateLayout();
        sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
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
