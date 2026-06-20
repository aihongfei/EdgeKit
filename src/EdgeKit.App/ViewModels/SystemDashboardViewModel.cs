using System.Collections.ObjectModel;
using EdgeKit.Services.Diagnostics;

namespace EdgeKit.App.ViewModels;

public sealed class SystemDashboardViewModel
{
    private readonly SystemResourceMonitorService _monitor;

    public SystemDashboardViewModel(SystemResourceMonitorService monitor)
    {
        _monitor = monitor;
    }

    public ObservableCollection<SystemResourcePoint> History { get; } = new();

    public SystemResourceSnapshot? Current { get; private set; }

    public SystemResourceSnapshot Capture()
    {
        var snapshot = _monitor.Capture();
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

        return snapshot;
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