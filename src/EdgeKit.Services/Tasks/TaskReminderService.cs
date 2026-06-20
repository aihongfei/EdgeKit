using EdgeKit.Core.Tasks;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace EdgeKit.Services.Tasks;

public sealed class TaskReminderService : IDisposable
{
    private const string NotificationIdPrefix = "task-";
    private const string Aumid = "EdgeKit";
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    private readonly ITaskBoardRepository _repository;
    private readonly HashSet<string> _delivered = new();
    private Timer? _timer;
    private bool _checking;
    private bool _startupNotified;
    private bool _disposed;

    public TaskReminderService(ITaskBoardRepository repository)
    {
        _repository = repository;
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        ShowStartupSummaryIfNeeded();

        _timer = new Timer(CheckTasks, null, PollingInterval, PollingInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private void ShowStartupSummaryIfNeeded()
    {
        if (_startupNotified)
        {
            return;
        }

        _startupNotified = true;

        try
        {
            var all = _repository.GetAll();
            var incomplete = all.Where(t => t.Status != TaskCardStatus.Done).ToList();

            if (incomplete.Count > 0)
            {
                ShowStartupToast(incomplete.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "显示启动待办任务汇总通知失败。");
            _startupNotified = false;
        }
    }

    private void CheckTasks(object? state)
    {
        if (_disposed || _checking)
        {
            return;
        }

        _checking = true;
        try
        {
            var now = DateTime.Now;
            var incomplete = _repository.GetAll()
                .Where(t => t.Status != TaskCardStatus.Done && t.DueLocal.HasValue)
                .ToList();

            foreach (var task in incomplete)
            {
                CheckTaskReminder(task, now);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "轮询任务提醒失败。");
        }
        finally
        {
            _checking = false;
        }
    }

    private void CheckTaskReminder(TaskCard task, DateTime now)
    {
        var due = task.DueLocal!.Value;

        var (suffix, title, detail) = due <= now
            ? ("due", $"任务已到期：{task.Title}", $"截止时间 {due:MM-dd HH:mm}")
            : GetUpcomingReminder(task, due, now);

        if (suffix is null)
        {
            return;
        }

        var id = $"{NotificationIdPrefix}{task.Id}-{suffix}";
        if (!_delivered.Add(id))
        {
            return;
        }

        ShowToast(id, title, detail);
    }

    private static (string? Suffix, string Title, string Detail) GetUpcomingReminder(TaskCard task, DateTime due, DateTime now)
    {
        var remaining = due - now;
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return minutes switch
        {
            <= 1 => ("1m", $"任务即将到期：{task.Title}", "还有 1 分钟截止"),
            <= 5 => ("5m", $"任务即将到期：{task.Title}", "还有 5 分钟截止"),
            <= 10 => ("10m", $"任务即将到期：{task.Title}", "还有 10 分钟截止"),
            _ => (null, string.Empty, string.Empty)
        };
    }

    private void ShowStartupToast(int count)
    {
        var xml = BuildToastXml(
            "待办任务提醒",
            $"当前有 {count} 个未完成任务，请及时处理。");
        var notification = new AppNotification(xml);
        AppNotificationManager.Default.Show(notification);
    }

    private static void ShowToast(string id, string title, string detail)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(title, detail));
            var toast = new ToastNotification(xml)
            {
                Tag = id
            };
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
            Log.Information("已发送任务通知 {Id}", id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送任务通知 {Id} 失败。", id);
        }
    }

    private static string BuildToastXml(string title, string detail)
    {
        return "<toast><visual><binding template=\"ToastGeneric\">" +
               "<text>" + EscapeXml(title) + "</text>" +
               "<text>" + EscapeXml(detail) + "</text>" +
               "</binding></visual></toast>";
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}