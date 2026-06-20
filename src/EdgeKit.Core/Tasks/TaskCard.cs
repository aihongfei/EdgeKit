namespace EdgeKit.Core.Tasks;

public enum TaskCardStatus
{
    Todo,
    InProgress,
    Done
}

public enum TaskCardPriority
{
    Low,
    Normal,
    High
}

public sealed record TaskCard(
    long Id,
    string Title,
    string Description,
    TaskCardStatus Status,
    TaskCardPriority Priority,
    DateTime? DueLocal,
    int SortOrder,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? CompletedUtc);