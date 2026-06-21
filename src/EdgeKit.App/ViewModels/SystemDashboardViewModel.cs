using System.Collections.ObjectModel;
using EdgeKit.Services.Diagnostics;

namespace EdgeKit.App.ViewModels;

public sealed class SystemDashboardViewModel
{
    private readonly SystemResourceMonitorService _monitor;
    private readonly object _captureLock = new();

    public SystemDashboardViewModel(SystemResourceMonitorService monitor)
    {
        _monitor = monitor;
    }

    public ObservableCollection<SystemResourcePoint> History { get; } = new();

    public Dictionary<string, ObservableCollection<DiskHistoryPoint>> DiskHistory { get; } = new();

    public SystemResourceSnapshot? Current { get; private set; }

    public SystemResourceSnapshot Capture()
    {
        var snapshot = CaptureSnapshot();
        ApplySnapshot(snapshot);
        return snapshot;
    }

    public SystemResourceSnapshot CaptureSnapshot()
    {
        lock (_captureLock)
        {
            return _monitor.Capture();
        }
    }

    public void ApplySnapshot(SystemResourceSnapshot snapshot)
    {
        Current = snapshot;

        History.Add(new SystemResourcePoint(
            snapshot.CapturedAt,
            snapshot.CpuPercent,
            snapshot.CpuFrequencyGHz,
            snapshot.MemoryPercent,
            snapshot.DiskPercent,
            ToMbps(snapshot.NetworkReceiveBytesPerSecond),
            ToMbps(snapshot.NetworkSendBytesPerSecond)));

        while (History.Count > 60)
        {
            History.RemoveAt(0);
        }

        foreach (var disk in snapshot.DiskDetails)
        {
            if (!DiskHistory.TryGetValue(disk.Letter, out var history))
            {
                history = new ObservableCollection<DiskHistoryPoint>();
                DiskHistory[disk.Letter] = history;
            }

            history.Add(new DiskHistoryPoint(snapshot.CapturedAt, disk.UsedPercent));

            while (history.Count > 60)
            {
                history.RemoveAt(0);
            }
        }
    }

    private static double ToMbps(long bytesPerSecond)
        => Math.Round(bytesPerSecond / (1024d * 1024d), 2);
}

public sealed record SystemResourcePoint(
    DateTime CapturedAt,
    double CpuPercent,
    double CpuFrequencyGHz,
    double MemoryPercent,
    double DiskPercent,
    double NetworkReceiveMbps,
    double NetworkSendMbps);

public sealed record DiskHistoryPoint(DateTime CapturedAt, double UsedPercent);