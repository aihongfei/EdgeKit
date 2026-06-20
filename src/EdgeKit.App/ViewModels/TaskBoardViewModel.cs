using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EdgeKit.Core.Tasks;

namespace EdgeKit.App.ViewModels;

public sealed class TaskBoardViewModel
{
    private readonly ITaskBoardRepository _repository;
    private TaskCardStatus? _filter;

    public TaskBoardViewModel(ITaskBoardRepository repository)
    {
        _repository = repository;
    }

    public ObservableCollection<TaskCardViewModel> AllCards { get; } = new();

    public ObservableCollection<object> GroupedItems { get; } = new();

    public ObservableCollection<TaskBoardFilterItem> Filters { get; } = new()
    {
        new() { Label = "全部", Status = null },
        new() { Label = "待办", Status = TaskCardStatus.Todo },
        new() { Label = "进行中", Status = TaskCardStatus.InProgress },
        new() { Label = "已完成", Status = TaskCardStatus.Done }
    };

    public TaskCardStatus? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            RebuildGroupedItems();
        }
    }

    public event EventHandler? RepositoryChanged
    {
        add => _repository.Changed += value;
        remove => _repository.Changed -= value;
    }

    public void Refresh()
    {
        AllCards.Clear();

        foreach (var card in _repository.GetAll().OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id))
        {
            AllCards.Add(new TaskCardViewModel(card));
        }

        foreach (var filter in Filters)
        {
            filter.Count = filter.Status.HasValue
                ? AllCards.Count(c => c.Status == filter.Status.Value)
                : AllCards.Count;
        }

        RebuildGroupedItems();
    }

    public void Add(string title, string description, DateTime? dueLocal, TaskCardPriority priority)
    {
        var now = DateTime.UtcNow;
        _repository.Add(new TaskCard(
            0,
            string.IsNullOrWhiteSpace(title) ? "新任务" : title.Trim(),
            description.Trim(),
            TaskCardStatus.Todo,
            priority,
            dueLocal,
            0,
            now,
            now,
            null));
    }

    public void Update(TaskCardViewModel card, string title, string description, DateTime? dueLocal, TaskCardPriority priority)
    {
        _repository.Update(card.Model with
        {
            Title = string.IsNullOrWhiteSpace(title) ? "新任务" : title.Trim(),
            Description = description.Trim(),
            DueLocal = dueLocal,
            Priority = priority,
            CompletedUtc = card.Status == TaskCardStatus.Done ? card.Model.CompletedUtc ?? DateTime.UtcNow : null
        });
    }

    public void Delete(TaskCardViewModel card) => _repository.Delete(card.Id);

    public void Move(TaskCardViewModel card, TaskCardStatus status)
    {
        if (card.Status == status)
        {
            return;
        }

        _repository.Move(card.Id, status, card.Model.SortOrder);
    }

    private void RebuildGroupedItems()
    {
        GroupedItems.Clear();

        var filtered = _filter.HasValue
            ? AllCards.Where(c => c.Status == _filter.Value)
            : AllCards.AsEnumerable();

        var groups = filtered
            .GroupBy(c => GetTimeBucket(c.DueLocal))
            .OrderBy(g => TimeBucketOrder(g.Key));

        foreach (var group in groups)
        {
            GroupedItems.Add(new TaskCardGroupHeader { Title = group.Key });

            foreach (var card in group)
            {
                GroupedItems.Add(card);
            }
        }
    }

    internal static string GetTimeBucket(DateTime? dueLocal)
    {
        if (dueLocal is null)
        {
            return "无截止日期";
        }

        var due = dueLocal.Value.Date;
        var today = DateTime.Today;

        if (due < today)
        {
            return "已逾期";
        }

        if (due == today)
        {
            return "今天到期";
        }

        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        var endOfWeek = today.AddDays(daysUntilSunday);

        if (due <= endOfWeek)
        {
            return "本周到期";
        }

        return "更晚";
    }

    private static int TimeBucketOrder(string bucket) => bucket switch
    {
        "已逾期" => 0,
        "今天到期" => 1,
        "本周到期" => 2,
        "更晚" => 3,
        "无截止日期" => 4,
        _ => 5
    };
}

public sealed class TaskCardGroupHeader
{
    public required string Title { get; init; }
}

public sealed class TaskBoardFilterItem : ObservableObject
{
    private int _count;

    public required string Label { get; init; }

    public TaskCardStatus? Status { get; init; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(Display));
            }
        }
    }

    public string Display => $"{Label} {Count}";
}

public sealed class TaskCardViewModel
{
    public TaskCardViewModel(TaskCard model)
    {
        Model = model;
    }

    public TaskCard Model { get; }

    public long Id => Model.Id;

    public string Title => Model.Title;

    public string Description => Model.Description;

    public TaskCardStatus Status => Model.Status;

    public TaskCardPriority Priority => Model.Priority;

    public DateTime? DueLocal => Model.DueLocal;

    public bool IsOverdue => DueLocal is not null && DueLocal.Value.Date < DateTime.Today;

    public string PriorityText => Priority switch
    {
        TaskCardPriority.High => "高优先级",
        TaskCardPriority.Low => "低优先级",
        _ => "普通"
    };

    public string TimeBucket => TaskBoardViewModel.GetTimeBucket(DueLocal);

    public bool IsHighPriority => Priority == TaskCardPriority.High;

    public bool IsLowPriority => Priority == TaskCardPriority.Low;

    public string StatusText => Status switch
    {
        TaskCardStatus.InProgress => "进行中",
        TaskCardStatus.Done => "已完成",
        _ => "待办"
    };

    public string DueText => DueLocal is null ? "无截止时间" : "截止 " + DueLocal.Value.ToString("MM-dd HH:mm");

    public string DescriptionPreview => string.IsNullOrWhiteSpace(Description) ? "无描述" : Description;
}