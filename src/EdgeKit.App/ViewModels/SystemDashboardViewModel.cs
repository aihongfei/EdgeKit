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

        var networkBytes = snapshot.NetworkReceiveBytesPerSecond + snapshot.NetworkSendBytesPerSecond;
        var networkScaled = Math.Clamp(networkBytes / 1024d, 0, 100);
        History.Add(new SystemResourcePoint(
            snapshot.CapturedAt,
            snapshot.CpuPercent,
            snapshot.MemoryPercent,
            snapshot.DiskPercent,
            networkScaled));

        while (History.Count > 60)
        {
            History.RemoveAt(0);
        }

        return snapshot;
    }
}

public sealed record SystemResourcePoint(
    DateTime CapturedAt,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent,
    double NetworkScaledPercent);