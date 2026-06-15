using EdgeKit.App.ViewModels;
using EdgeKit.Core.Agent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.System;

namespace EdgeKit.App.Views;

public sealed partial class AgentChatPage : Page
{
    private IAgentService? _agent;
    private long _selectedConversationId;
    private bool _loading;

    public AgentChatPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AgentChatPageParameter parameter)
        {
            _agent = parameter.AgentService;
            _agent.Changed += OnAgentChanged;
            SelectMode(_agent.GetSettings().DefaultMode);
            LoadConversations();
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
        => DispatcherQueue.TryEnqueue(() =>
        {
            LoadConversations(keepSelection: true);
            LoadConversationDetail();
        });

    private void LoadConversations(bool keepSelection = false)
    {
        if (_agent is null)
        {
            return;
        }

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
            MessageList.ItemsSource = null;
            ToolCallList.ItemsSource = null;
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

        MessageList.ItemsSource = detail.Messages.Select(m => new AgentMessageViewModel(m)).ToList();
        ToolCallList.ItemsSource = detail.ToolCalls.Select(t => new AgentToolCallViewModel(t)).Reverse().ToList();
        SelectMode(detail.Conversation.Mode);
    }

    private void OnConversationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is not AgentConversationViewModel item)
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
        if (_agent is null)
        {
            return;
        }

        var conversation = _agent.CreateConversation(GetSelectedMode());
        _selectedConversationId = conversation.Id;
        LoadConversations(keepSelection: true);
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendCurrentPromptAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent send failed unexpectedly");
            SetBusy(false);
            ShowStatus("发送失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            try
            {
                await SendCurrentPromptAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Agent send failed unexpectedly");
                SetBusy(false);
                ShowStatus("发送失败", ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private async Task SendCurrentPromptAsync()
    {
        if (_agent is null)
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
        AgentSendResult result;
        try
        {
            result = await _agent.SendAsync(_selectedConversationId, text);
        }
        finally
        {
            SetBusy(false);
        }

        if (!result.Success)
        {
            ShowStatus("发送失败", result.ErrorMessage, InfoBarSeverity.Error);
        }

        LoadConversations(keepSelection: true);
        LoadConversationDetail();
    }

    private async void OnApproveToolClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || (sender as FrameworkElement)?.Tag is not long id)
        {
            return;
        }

        try
        {
            SetBusy(true);
            var result = await _agent.ApproveToolCallAsync(id);
            ShowStatus(result.Success ? "工具已执行" : "工具执行失败", result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            LoadConversationDetail();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Agent tool approval failed unexpectedly");
            ShowStatus("工具执行失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnRejectToolClick(object sender, RoutedEventArgs e)
    {
        if (_agent is null || (sender as FrameworkElement)?.Tag is not long id)
        {
            return;
        }

        var result = _agent.RejectToolCall(id);
        ShowStatus(result.Success ? "已拒绝" : "拒绝失败", result.Message, result.Success ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
        LoadConversationDetail();
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

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        PromptBox.IsEnabled = !busy;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
