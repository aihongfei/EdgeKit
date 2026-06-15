using System.ComponentModel;
using EdgeKit.Core.Agent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EdgeKit.App.ViewModels;

public enum AgentTimelineItemKind
{
    Message,
    ToolCall
}

public sealed class AgentTimelineItemViewModel : INotifyPropertyChanged
{
    private AgentMessage? _message;
    private AgentToolCall? _toolCall;
    private string _streamedContent = string.Empty;
    private bool _isStreaming;
    private bool _isExpanded;
    private bool _hasUserToggledExpansion;

    public AgentTimelineItemViewModel(AgentMessage message)
    {
        _message = message;
    }

    public AgentTimelineItemViewModel(AgentToolCall toolCall)
    {
        _toolCall = toolCall;
        _isExpanded = ShouldAutoExpand(toolCall);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentTimelineItemKind Kind => _message is not null ? AgentTimelineItemKind.Message : AgentTimelineItemKind.ToolCall;

    public AgentMessage? Message => _message;

    public AgentToolCall? ToolCall => _toolCall;

    public long MessageId => _message?.Id ?? 0;

    public long ToolCallId => _toolCall?.Id ?? 0;

    public Visibility MessageVisibility => Kind == AgentTimelineItemKind.Message ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ToolVisibility => Kind == AgentTimelineItemKind.ToolCall ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => _isStreaming
        && string.IsNullOrWhiteSpace(Content)
        && string.IsNullOrWhiteSpace(ActivityText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ContentVisibility => string.IsNullOrWhiteSpace(Content) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ActivityVisibility => _message?.Role == AgentMessageRole.Assistant && !string.IsNullOrWhiteSpace(ActivityText)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ActivityProgressVisibility => ActivityVisibility == Visibility.Visible && _message?.Status == AgentMessageStatus.Pending
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ActivityIconVisibility => ActivityVisibility == Visibility.Visible && _message?.Status != AgentMessageStatus.Pending
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ToolActionVisibility => CanApprove ? Visibility.Visible : Visibility.Collapsed;

    public string RoleText => _message?.Role switch
    {
        AgentMessageRole.User => "你",
        AgentMessageRole.Assistant => "智能体",
        AgentMessageRole.Tool => "工具",
        AgentMessageRole.System => "系统",
        _ => string.Empty
    };

    public bool IsUser => _message?.Role == AgentMessageRole.User;

    public HorizontalAlignment BubbleAlignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush BubbleBackground => Resource(IsUser ? "EdgeAccentSoftBrush" : "EdgePanelBrush");

    public Brush BubbleBorderBrush => Resource("EdgeLineBrush");

    public string Content
    {
        get
        {
            if (_message is null)
            {
                return string.Empty;
            }

            if (_message.Status == AgentMessageStatus.Failed)
            {
                return "失败: " + _message.Error;
            }

            return _isStreaming ? _streamedContent : _message.Content;
        }
    }

    public string ActivityText => _message?.ActivityText ?? string.Empty;

    public string TimeText
        => (_message?.CreatedUtc ?? _toolCall?.CreatedUtc ?? DateTime.UtcNow).ToLocalTime().ToString("HH:mm");

    public string ToolTitle => _toolCall is null ? string.Empty : $"{_toolCall.ToolName} · {RiskText}";

    public string ToolStatusText => _toolCall is null ? string.Empty : $"{ApprovalText} / {ExecutionText}";

    public string ToolSummary
    {
        get
        {
            if (_toolCall is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(_toolCall.Error))
            {
                return _toolCall.Error;
            }

            if (!string.IsNullOrWhiteSpace(_toolCall.ResultSummary))
            {
                return Trim(_toolCall.ResultSummary, 180);
            }

            return Trim(_toolCall.ArgumentsSummary, 180);
        }
    }

    public string ToolDetail
    {
        get
        {
            if (_toolCall is null)
            {
                return string.Empty;
            }

            return "参数:" + Environment.NewLine +
                EmptyFallback(_toolCall.ArgumentsSummary) + Environment.NewLine +
                Environment.NewLine +
                "结果:" + Environment.NewLine +
                EmptyFallback(_toolCall.ResultSummary) + Environment.NewLine +
                Environment.NewLine +
                "错误:" + Environment.NewLine +
                EmptyFallback(_toolCall.Error);
        }
    }

    public bool CanApprove => _toolCall?.ApprovalStatus == AgentToolApprovalStatus.Pending;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            _hasUserToggledExpansion = true;
            Raise(nameof(IsExpanded));
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value)
            {
                return;
            }

            _isStreaming = value;
            RaiseMessageProperties();
        }
    }

    public string StreamedContent
    {
        get => _streamedContent;
        set
        {
            if (_streamedContent == value)
            {
                return;
            }

            _streamedContent = value;
            RaiseMessageProperties();
        }
    }

    public void UpdateMessage(AgentMessage message)
    {
        _message = message;
        _streamedContent = message.Content;
        _isStreaming = message.Status == AgentMessageStatus.Pending;
        RaiseAll();
    }

    public void UpdateToolCall(AgentToolCall toolCall)
    {
        var previous = _toolCall;
        _toolCall = toolCall;
        ApplyAutomaticExpansion(previous, toolCall);
        RaiseAll();
    }

    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (_streamedContent == "正在思考...")
        {
            _streamedContent = string.Empty;
        }

        _streamedContent += delta;
        RaiseMessageProperties();
    }

    private string RiskText => _toolCall?.Risk switch
    {
        AgentToolRisk.ReadOnly => "只读",
        AgentToolRisk.OpensExternal => "打开外部",
        AgentToolRisk.UserWrite => "用户写入",
        AgentToolRisk.SystemWrite => "系统写入",
        AgentToolRisk.Destructive => "危险",
        _ => _toolCall?.Risk.ToString() ?? string.Empty
    };

    private string ApprovalText => _toolCall?.ApprovalStatus switch
    {
        AgentToolApprovalStatus.NotRequired => "无需确认",
        AgentToolApprovalStatus.Pending => "待确认",
        AgentToolApprovalStatus.Approved => "已确认",
        AgentToolApprovalStatus.Rejected => "已拒绝",
        _ => _toolCall?.ApprovalStatus.ToString() ?? string.Empty
    };

    private string ExecutionText => _toolCall?.ExecutionStatus switch
    {
        AgentToolExecutionStatus.Pending => "待执行",
        AgentToolExecutionStatus.Running => "执行中",
        AgentToolExecutionStatus.Succeeded => "成功",
        AgentToolExecutionStatus.Failed => "失败",
        AgentToolExecutionStatus.Skipped => "跳过",
        _ => _toolCall?.ExecutionStatus.ToString() ?? string.Empty
    };

    private static bool ShouldAutoExpand(AgentToolCall toolCall)
        => toolCall.ApprovalStatus == AgentToolApprovalStatus.Pending
            || toolCall.ExecutionStatus is AgentToolExecutionStatus.Pending
                or AgentToolExecutionStatus.Running
                or AgentToolExecutionStatus.Failed;

    private void ApplyAutomaticExpansion(AgentToolCall? previous, AgentToolCall current)
    {
        if (current.ApprovalStatus == AgentToolApprovalStatus.Pending
            || current.ExecutionStatus is AgentToolExecutionStatus.Pending or AgentToolExecutionStatus.Running or AgentToolExecutionStatus.Failed)
        {
            SetExpandedFromState(true);
        }
    }

    public void CollapseCompletedToolCall()
    {
        if (_toolCall is null
            || _toolCall.ApprovalStatus == AgentToolApprovalStatus.Pending
            || _toolCall.ExecutionStatus is not (AgentToolExecutionStatus.Succeeded or AgentToolExecutionStatus.Skipped))
        {
            return;
        }

        SetExpandedFromState(false);
    }

    private void SetExpandedFromState(bool expanded)
    {
        if (!expanded && _hasUserToggledExpansion)
        {
            return;
        }

        if (_isExpanded == expanded)
        {
            return;
        }

        _isExpanded = expanded;
        Raise(nameof(IsExpanded));
    }

    private void RaiseMessageProperties()
    {
        Raise(nameof(Content));
        Raise(nameof(ActivityText));
        Raise(nameof(ContentVisibility));
        Raise(nameof(LoadingVisibility));
        Raise(nameof(ActivityVisibility));
        Raise(nameof(ActivityProgressVisibility));
        Raise(nameof(ActivityIconVisibility));
        Raise(nameof(IsStreaming));
        Raise(nameof(StreamedContent));
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(Kind),
            nameof(Message),
            nameof(ToolCall),
            nameof(MessageId),
            nameof(ToolCallId),
            nameof(MessageVisibility),
            nameof(ToolVisibility),
            nameof(LoadingVisibility),
            nameof(ContentVisibility),
            nameof(ActivityVisibility),
            nameof(ActivityProgressVisibility),
            nameof(ActivityIconVisibility),
            nameof(ToolActionVisibility),
            nameof(RoleText),
            nameof(Content),
            nameof(ActivityText),
            nameof(TimeText),
            nameof(ToolTitle),
            nameof(ToolStatusText),
            nameof(ToolSummary),
            nameof(ToolDetail),
            nameof(CanApprove),
            nameof(IsExpanded)
        })
        {
            Raise(name);
        }
    }

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string EmptyFallback(string value)
        => string.IsNullOrWhiteSpace(value) ? "无" : value;

    private static Brush Resource(string key)
        => (Brush)Application.Current.Resources[key];

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
