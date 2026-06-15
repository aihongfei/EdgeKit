using EdgeKit.Core.Agent;

namespace EdgeKit.App.ViewModels;

public sealed class AgentToolCallViewModel
{
    public AgentToolCallViewModel(AgentToolCall call)
    {
        Call = call;
    }

    public AgentToolCall Call { get; }

    public long Id => Call.Id;

    public string Title => $"{Call.ToolName} · {RiskText}";

    public string Detail
        => $"{ApprovalText} / {ExecutionText}\n参数: {Call.ArgumentsSummary}\n结果: {ResultText}";

    public bool CanApprove => Call.ApprovalStatus == AgentToolApprovalStatus.Pending;

    public string RiskText => Call.Risk switch
    {
        AgentToolRisk.ReadOnly => "只读",
        AgentToolRisk.OpensExternal => "打开外部",
        AgentToolRisk.UserWrite => "用户写入",
        AgentToolRisk.SystemWrite => "系统写入",
        AgentToolRisk.Destructive => "危险",
        _ => Call.Risk.ToString()
    };

    public string ApprovalText => Call.ApprovalStatus switch
    {
        AgentToolApprovalStatus.NotRequired => "无需确认",
        AgentToolApprovalStatus.Pending => "待确认",
        AgentToolApprovalStatus.Approved => "已确认",
        AgentToolApprovalStatus.Rejected => "已拒绝",
        _ => Call.ApprovalStatus.ToString()
    };

    public string ExecutionText => Call.ExecutionStatus switch
    {
        AgentToolExecutionStatus.Pending => "待执行",
        AgentToolExecutionStatus.Running => "执行中",
        AgentToolExecutionStatus.Succeeded => "成功",
        AgentToolExecutionStatus.Failed => "失败",
        AgentToolExecutionStatus.Skipped => "跳过",
        _ => Call.ExecutionStatus.ToString()
    };

    public string ResultText
        => !string.IsNullOrWhiteSpace(Call.ResultSummary)
            ? Call.ResultSummary
            : !string.IsNullOrWhiteSpace(Call.Error)
                ? Call.Error
                : "暂无";
}
