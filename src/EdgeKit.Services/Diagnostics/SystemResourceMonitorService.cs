using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using EdgeKit.Native;

namespace EdgeKit.Services.Diagnostics;

public sealed class SystemResourceMonitorService
{
    private CpuTimes? _lastCpuTimes;
    private DateTime _lastSampleUtc;
    private long _lastNetworkReceived;
    private long _lastNetworkSent;

    public SystemResourceSnapshot Capture()
    {
        var nowUtc = DateTime.UtcNow;
        var cpu = ReadCpuUsage();
        var cpuFrequency = ReadCpuFrequencyGHz();
        var memory = ReadMemory();
        var disk = ReadDisk();
        var network = ReadNetwork(nowUtc);

        _lastSampleUtc = nowUtc;

        return new SystemResourceSnapshot(
            DateTime.Now,
            cpu,
            cpuFrequency,
            memory.UsedPercent,
            memory.UsedText,
            memory.AvailableText,
            disk.Aggregate.UsedPercent,
            disk.Aggregate.UsedText,
            disk.Aggregate.FreeText,
            disk.Details,
            network.ReceiveBytesPerSecond,
            network.SendBytesPerSecond,
            FormatBytes(network.ReceiveBytesPerSecond) + "/s",
            FormatBytes(network.SendBytesPerSecond) + "/s");
    }

    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var current = new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        var previous = _lastCpuTimes;
        _lastCpuTimes = current;

        if (previous is null)
        {
            return 0;
        }

        var idleDiff = current.Idle - previous.Value.Idle;
        var kernelDiff = current.Kernel - previous.Value.Kernel;
        var userDiff = current.User - previous.Value.User;
        var total = kernelDiff + userDiff;
        if (total == 0)
        {
            return 0;
        }

        var used = Math.Clamp((total - idleDiff) * 100d / total, 0, 100);
        return Math.Round(used, 1);
    }

    private static double ReadCpuFrequencyGHz()
    {
        try
        {
            var processorCount = Environment.ProcessorCount;
            if (processorCount <= 0)
            {
                return 0;
            }

            var buffer = new NativeMethods.PROCESSOR_POWER_INFORMATION[processorCount];
            var size = Marshal.SizeOf<NativeMethods.PROCESSOR_POWER_INFORMATION>() * processorCount;
            var result = NativeMethods.CallNtPowerInformation(
                NativeMethods.ProcessorInformation,
                nint.Zero,
                0,
                buffer,
                size);

            if (result != 0)
            {
                return 0;
            }

            var avgMhz = buffer.Average(p => p.CurrentMhz);
            return Math.Round(avgMhz / 1000d, 2);
        }
        catch
        {
            return 0;
        }
    }

    private static MemoryUsage ReadMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX();
        status.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();

        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            return new MemoryUsage(0, "未知", "未知");
        }

        var total = (long)status.ullTotalPhys;
        var available = (long)status.ullAvailPhys;
        var used = Math.Max(0, total - available);
        var percent = Math.Round(used * 100d / total, 1);
        return new MemoryUsage(percent, FormatBytes(used), FormatBytes(available));
    }

    private static (DiskUsage Aggregate, IReadOnlyList<DiskInfo> Details) ReadDisk()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0)
                .ToArray();
            if (drives.Length == 0)
            {
                return (new DiskUsage(0, "未知", "未知"), Array.Empty<DiskInfo>());
            }

            var total = drives.Sum(d => d.TotalSize);
            var free = drives.Sum(d => d.AvailableFreeSpace);
            var used = Math.Max(0, total - free);
            var percent = Math.Round(used * 100d / total, 1);
            var aggregate = new DiskUsage(percent, FormatBytes(used), FormatBytes(free));

            var details = drives
                .Select(d =>
                {
                    var driveTotal = d.TotalSize;
                    var driveFree = d.AvailableFreeSpace;
                    var driveUsed = Math.Max(0, driveTotal - driveFree);
                    var drivePercent = Math.Round(driveUsed * 100d / driveTotal, 1);
                    return new DiskInfo(
                        d.Name.TrimEnd('\\'),
                        drivePercent,
                        FormatBytes(driveUsed),
                        FormatBytes(driveFree),
                        FormatBytes(driveTotal));
                })
                .ToArray();

            return (aggregate, details);
        }
        catch (IOException)
        {
            return (new DiskUsage(0, "未知", "未知"), Array.Empty<DiskInfo>());
        }
        catch (UnauthorizedAccessException)
        {
            return (new DiskUsage(0, "未知", "未知"), Array.Empty<DiskInfo>());
        }
    }

    private NetworkUsage ReadNetwork(DateTime nowUtc)
    {
        var received = 0L;
        var sent = 0L;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var stats = adapter.GetIPv4Statistics();
                received += Math.Max(0, stats.BytesReceived);
                sent += Math.Max(0, stats.BytesSent);
            }
            catch (NetworkInformationException)
            {
            }
        }

        var seconds = Math.Max(1, (nowUtc - _lastSampleUtc).TotalSeconds);
        var receiveRate = _lastNetworkReceived <= 0 ? 0 : (long)Math.Max(0, (received - _lastNetworkReceived) / seconds);
        var sendRate = _lastNetworkSent <= 0 ? 0 : (long)Math.Max(0, (sent - _lastNetworkSent) / seconds);

        _lastNetworkReceived = received;
        _lastNetworkSent = sent;

        return new NetworkUsage(receiveRate, sendRate);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static ulong ToUInt64(FileTime fileTime)
        => ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private readonly record struct MemoryUsage(double UsedPercent, string UsedText, string AvailableText);

    private readonly record struct DiskUsage(double UsedPercent, string UsedText, string FreeText);

    private readonly record struct NetworkUsage(long ReceiveBytesPerSecond, long SendBytesPerSecond);
}

public sealed record DiskInfo(
    string Letter,
    double UsedPercent,
    string UsedText,
    string FreeText,
    string TotalText);

public sealed record SystemResourceSnapshot(
    DateTime CapturedAt,
    double CpuPercent,
    double CpuFrequencyGHz,
    double MemoryPercent,
    string MemoryUsedText,
    string MemoryAvailableText,
    double DiskPercent,
    string DiskUsedText,
    string DiskFreeText,
    IReadOnlyList<DiskInfo> DiskDetails,
    long NetworkReceiveBytesPerSecond,
    long NetworkSendBytesPerSecond,
    string NetworkReceiveText,
    string NetworkSendText);