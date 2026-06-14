using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using EdgeKit.Native;
using Microsoft.Win32;

namespace EdgeKit.Services.Diagnostics;

/// <summary>
/// Diagnostics used by the system tools workspace.
/// </summary>
public sealed class SystemDiagnosticsService
{
    private static readonly TimeSpan PublicIpTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = PublicIpTimeout
    };

    public NetworkSnapshot GetNetworkSnapshot(string? publicIp = null, string? publicIpStatus = null)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUserVisibleAdapter)
            .Select(ReadAdapter)
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new NetworkSnapshot(
            DateTime.Now,
            NetworkInterface.GetIsNetworkAvailable(),
            adapters,
            publicIp ?? string.Empty,
            publicIpStatus ?? "未刷新");
    }

    public ProxySnapshot GetProxySnapshot()
    {
        var entries = new List<ProxyEntry>();
        var registryProxy = ReadRegistryProxy();
        if (registryProxy is not null)
        {
            entries.Add(registryProxy);
        }

        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                entries.Add(new ProxyEntry(name, value, "环境变量"));
            }
        }

        AddDefaultProxy(entries, "http://edgekit.local/");
        AddDefaultProxy(entries, "https://edgekit.local/");

        return new ProxySnapshot(DateTime.Now, entries);
    }

    public async Task<string> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        var value = await _httpClient
            .GetStringAsync("https://api.ipify.org", cancellationToken)
            .ConfigureAwait(false);

        return value.Trim();
    }

    public async Task<PingProbeResult> PingAsync(string host, CancellationToken cancellationToken = default)
    {
        host = string.IsNullOrWhiteSpace(host) ? "8.8.8.8" : host.Trim();

        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host, (int)PingTimeout.TotalMilliseconds)
                .WaitAsync(PingTimeout + TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);

            return new PingProbeResult(
                host,
                reply.Status == IPStatus.Success,
                reply.RoundtripTime,
                reply.Address?.ToString() ?? string.Empty,
                reply.Status.ToString(),
                string.Empty);
        }
        catch (Exception ex) when (ex is PingException or SocketException or TimeoutException or TaskCanceledException or ArgumentException)
        {
            return new PingProbeResult(host, false, 0, string.Empty, "Failed", ex.Message);
        }
    }

    public async Task<DnsProbeResult> ResolveDnsAsync(string host, CancellationToken cancellationToken = default)
    {
        host = string.IsNullOrWhiteSpace(host) ? "www.microsoft.com" : host.Trim();
        var watch = Stopwatch.StartNew();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
                .WaitAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();
            return new DnsProbeResult(
                host,
                true,
                watch.ElapsedMilliseconds,
                addresses.Select(a => a.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                string.Empty);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or TaskCanceledException)
        {
            watch.Stop();
            return new DnsProbeResult(host, false, watch.ElapsedMilliseconds, Array.Empty<string>(), ex.Message);
        }
    }

    public async Task<TcpProbeResult> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        host = string.IsNullOrWhiteSpace(host) ? "www.microsoft.com" : host.Trim();
        port = port <= 0 ? 443 : Math.Min(port, 65535);
        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken)
                .AsTask()
                .WaitAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();
            return new TcpProbeResult(host, port, true, watch.ElapsedMilliseconds, string.Empty);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or TaskCanceledException)
        {
            watch.Stop();
            return new TcpProbeResult(host, port, false, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    public SystemSnapshot GetSystemSnapshot()
    {
        var memory = ReadMemory();
        var drives = ReadDrives();

        return new SystemSnapshot(
            DateTime.Now,
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            Environment.UserName,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            ReadCpuName(),
            Environment.ProcessorCount,
            memory.Total,
            memory.Available,
            drives);
    }

    public IReadOnlyList<PortEntry> GetPortEntries()
    {
        var entries = new List<PortEntry>();

        entries.AddRange(GetTcpPortEntries());
        entries.AddRange(GetUdpPortEntries());

        if (entries.Count == 0)
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            entries.AddRange(properties.GetActiveTcpListeners()
                .Select(e => new PortEntry("TCP", e.Address.ToString(), e.Port, "监听", string.Empty, 0, "不可用", string.Empty)));
            entries.AddRange(properties.GetActiveUdpListeners()
                .Select(e => new PortEntry("UDP", e.Address.ToString(), e.Port, "监听", string.Empty, 0, "不可用", string.Empty)));
        }

        return entries
            .OrderBy(e => e.Port)
            .ThenBy(e => e.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.LocalAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public StartupServiceSnapshot GetStartupItemsSnapshot()
    {
        return new StartupServiceSnapshot(
            DateTime.Now,
            ReadStartupItems(),
            Array.Empty<WindowsServiceEntry>());
    }

    public IReadOnlyList<WindowsServiceEntry> SearchWindowsServices(string query)
        => string.IsNullOrWhiteSpace(query)
            ? Array.Empty<WindowsServiceEntry>()
            : ReadWindowsServices(query.Trim());

    public KillProcessResult KillPortOwner(PortEntry entry)
    {
        if (!entry.CanKill)
        {
            return new KillProcessResult(false, "该端口没有可结束的进程");
        }

        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            var name = string.IsNullOrWhiteSpace(entry.ProcessName)
                ? process.ProcessName
                : entry.ProcessName;
            process.Kill(entireProcessTree: false);
            process.WaitForExit(2000);
            return new KillProcessResult(true, $"已结束 {name} (PID {entry.ProcessId})");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new KillProcessResult(false, "结束进程失败：" + ex.Message);
        }
    }

    public ToolActionResult OpenProcessDirectory(PortEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProcessDirectory) || !Directory.Exists(entry.ProcessDirectory))
        {
            return new ToolActionResult(false, "进程目录不可用");
        }

        return OpenDirectory(entry.ProcessDirectory);
    }

    public ToolActionResult OpenPortLocalhost(PortEntry entry)
        => entry.CanOpenLocalhost
            ? OpenUrl(entry.LocalhostUrl)
            : new ToolActionResult(false, "该端口不是可打开的 TCP 监听端口");

    public ToolActionResult OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolActionResult(false, "URL 为空");
        }

        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            return new ToolActionResult(true, "已打开");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ToolActionResult(false, "打开失败：" + ex.Message);
        }
    }

    public string BuildNetworkReport(NetworkSnapshot snapshot, ProxySnapshot? proxy = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 网络诊断");
        builder.AppendLine($"刷新时间: {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"网络可用: {(snapshot.IsNetworkAvailable ? "是" : "否")}");
        builder.AppendLine($"公网 IP: {(string.IsNullOrWhiteSpace(snapshot.PublicIp) ? snapshot.PublicIpStatus : snapshot.PublicIp)}");
        if (proxy is not null)
        {
            builder.AppendLine($"代理: {proxy.SummaryText}");
        }

        builder.AppendLine();

        foreach (var adapter in snapshot.Adapters)
        {
            builder.AppendLine($"[{adapter.Name}] {adapter.StatusText}");
            builder.AppendLine($"  类型: {adapter.Type}");
            builder.AppendLine($"  IPv4: {adapter.Ipv4Text}");
            builder.AppendLine($"  IPv6: {adapter.Ipv6Text}");
            builder.AppendLine($"  网关: {adapter.GatewayText}");
            builder.AppendLine($"  DNS: {adapter.DnsText}");
        }

        return builder.ToString().TrimEnd();
    }

    public string BuildSystemReport(SystemSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 系统快照");
        builder.AppendLine($"刷新时间: {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"系统: {snapshot.OsDescription}");
        builder.AppendLine($"机器名: {snapshot.MachineName}");
        builder.AppendLine($"用户: {snapshot.UserName}");
        builder.AppendLine($"开机时长: {snapshot.UptimeText}");
        builder.AppendLine($"CPU: {snapshot.CpuName} ({snapshot.ProcessorCount} 线程)");
        builder.AppendLine($"内存: {snapshot.MemoryText}");
        builder.AppendLine($".NET: {snapshot.DotnetVersion}");
        builder.AppendLine();
        builder.AppendLine("磁盘:");
        foreach (var drive in snapshot.Drives)
        {
            builder.AppendLine($"  {drive.Name} 可用 {drive.Free} / 总计 {drive.Total} ({drive.Format})");
        }

        return builder.ToString().TrimEnd();
    }

    public string BuildPortReport(IReadOnlyList<PortEntry> portEntries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 端口占用");
        builder.AppendLine($"刷新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"端口数量: {portEntries.Count}");
        foreach (var entry in portEntries)
        {
            builder.AppendLine(entry.ToReportText());
        }

        return builder.ToString().TrimEnd();
    }

    public string BuildStartupServiceReport(StartupServiceSnapshot snapshot)
        => BuildStartupServiceReport(snapshot.StartupItems, snapshot.Services, string.Empty);

    public string BuildStartupServiceReport(
        IReadOnlyList<StartupItemEntry> startupItems,
        IReadOnlyList<WindowsServiceEntry> services,
        string serviceQuery)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 启动服务检查");
        builder.AppendLine($"刷新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine("启动项:");
        foreach (var item in startupItems)
        {
            builder.AppendLine($"  [{item.Source}] {item.Name}: {item.Command}");
        }

        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(serviceQuery)
            ? "Windows 服务: 未搜索"
            : $"Windows 服务搜索: {serviceQuery}");
        foreach (var service in services)
        {
            builder.AppendLine($"  {service.DisplayName} ({service.ServiceName}) · {service.Status} · {service.StartType} · {service.Path}");
        }

        return builder.ToString().TrimEnd();
    }

    public string BuildSystemSummary(SystemSnapshot snapshot)
    {
        var drives = snapshot.Drives.Count == 0
            ? "磁盘未知"
            : string.Join("; ", snapshot.Drives.Select(d => $"{d.Name} 可用 {d.Free}/{d.Total}"));

        return string.Join(Environment.NewLine, new[]
        {
            $"系统: {snapshot.OsDescription}",
            $"机器: {snapshot.MachineName} · 用户: {snapshot.UserName}",
            $"开机: {snapshot.UptimeText} · 架构: {snapshot.Architecture} · .NET: {snapshot.DotnetVersion}",
            $"CPU: {snapshot.CpuName} ({snapshot.ProcessorCount} 线程)",
            $"内存: {snapshot.MemoryText}",
            $"磁盘: {drives}"
        });
    }

    private static IReadOnlyList<DriveSnapshot> ReadDrives()
        => DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveSnapshot(
                d.Name,
                FormatBytes(d.TotalSize),
                FormatBytes(d.AvailableFreeSpace),
                d.DriveFormat))
            .ToArray();

    private static NetworkAdapterInfo ReadAdapter(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        var addresses = properties.UnicastAddresses
            .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(a => a.Address.ToString())
            .ToArray();

        var ipv4 = properties.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToArray();

        var ipv6 = properties.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(a => a.Address.ToString())
            .ToArray();

        var gateways = properties.GatewayAddresses
            .Select(g => g.Address.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "0.0.0.0")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dns = properties.DnsAddresses
            .Select(d => d.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NetworkAdapterInfo(
            adapter.Name,
            adapter.Description,
            adapter.NetworkInterfaceType.ToString(),
            adapter.OperationalStatus.ToString(),
            adapter.OperationalStatus == OperationalStatus.Up,
            FormatSpeed(adapter.Speed),
            string.Join(", ", addresses),
            string.Join(", ", ipv4),
            string.Join(", ", ipv6),
            string.Join(", ", gateways),
            string.Join(", ", dns));
    }

    private static IReadOnlyList<PortEntry> GetTcpPortEntries()
    {
        var entries = new List<PortEntry>();
        var bufferLength = 0;
        _ = GetExtendedTcpTable(nint.Zero, ref bufferLength, true, AfInet, TcpTableClass.OwnerPidAll, 0);
        if (bufferLength <= 0)
        {
            return entries;
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref bufferLength, true, AfInet, TcpTableClass.OwnerPidAll, 0);
            if (result != 0)
            {
                return entries;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPtr = nint.Add(buffer, sizeof(uint));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(nint.Add(rowPtr, i * rowSize));
                var pid = unchecked((int)row.OwningPid);
                var process = ReadProcessInfo(pid);
                entries.Add(new PortEntry(
                    "TCP",
                    FormatIp(row.LocalAddr),
                    ConvertPort(row.LocalPort),
                    FormatTcpState(row.State),
                    $"{FormatIp(row.RemoteAddr)}:{ConvertPort(row.RemotePort)}",
                    pid,
                    process.Name,
                    process.Path));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return entries;
    }

    private static IReadOnlyList<PortEntry> GetUdpPortEntries()
    {
        var entries = new List<PortEntry>();
        var bufferLength = 0;
        _ = GetExtendedUdpTable(nint.Zero, ref bufferLength, true, AfInet, UdpTableClass.OwnerPid, 0);
        if (bufferLength <= 0)
        {
            return entries;
        }

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            var result = GetExtendedUdpTable(buffer, ref bufferLength, true, AfInet, UdpTableClass.OwnerPid, 0);
            if (result != 0)
            {
                return entries;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var rowPtr = nint.Add(buffer, sizeof(uint));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(nint.Add(rowPtr, i * rowSize));
                var pid = unchecked((int)row.OwningPid);
                var process = ReadProcessInfo(pid);
                entries.Add(new PortEntry(
                    "UDP",
                    FormatIp(row.LocalAddr),
                    ConvertPort(row.LocalPort),
                    "监听",
                    string.Empty,
                    pid,
                    process.Name,
                    process.Path));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return entries;
    }

    private static (string Name, string Path) ReadProcessInfo(int processId)
    {
        if (processId <= 0)
        {
            return ("不可用", string.Empty);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            var path = string.Empty;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                path = string.Empty;
            }

            return (name, path);
        }
        catch
        {
            return ("不可用", string.Empty);
        }
    }

    private static ToolActionResult OpenDirectory(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
            return new ToolActionResult(true, "已打开目录");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ToolActionResult(false, "打开目录失败：" + ex.Message);
        }
    }

    private static IReadOnlyList<StartupItemEntry> ReadStartupItems()
    {
        var items = new List<StartupItemEntry>();
        ReadStartupFolder(items, "用户启动文件夹", Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        ReadStartupFolder(items, "公共启动文件夹", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

        ReadRunKey(items, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run");
        ReadRunKey(items, RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce");
        ReadRunKey(items, RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run");
        ReadRunKey(items, RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce");
        if (Environment.Is64BitOperatingSystem)
        {
            ReadRunKey(items, RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Wow6432 Run");
            ReadRunKey(items, RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM Wow6432 RunOnce");
        }

        return items
            .GroupBy(i => i.Source + "\0" + i.Name + "\0" + i.Command, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(i => i.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void ReadStartupFolder(List<StartupItemEntry> items, string source, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                items.Add(new StartupItemEntry(
                    Path.GetFileNameWithoutExtension(file),
                    source,
                    file,
                    path));
            }
        }
        catch
        {
            // A startup folder can be denied by policy; keep the inspector read-only and resilient.
        }
    }

    private static void ReadRunKey(
        List<StartupItemEntry> items,
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string source)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames())
            {
                items.Add(new StartupItemEntry(
                    string.IsNullOrWhiteSpace(name) ? "(默认)" : name,
                    source,
                    key.GetValue(name)?.ToString() ?? string.Empty,
                    $"{hive}\\{subKey}"));
            }
        }
        catch
        {
            // Some HKLM views can be restricted; omit inaccessible values.
        }
    }

    private static IReadOnlyList<WindowsServiceEntry> ReadWindowsServices(string query)
    {
        var handle = OpenSCManager(null, null, ScManagerEnumerateService);
        if (handle == nint.Zero)
        {
            return Array.Empty<WindowsServiceEntry>();
        }

        try
        {
            var bytesNeeded = 0;
            var servicesReturned = 0;
            var resumeHandle = 0;
            _ = EnumServicesStatusEx(
                handle,
                ScEnumProcessInfo,
                ServiceWin32,
                ServiceStateAll,
                nint.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            if (bytesNeeded <= 0)
            {
                return Array.Empty<WindowsServiceEntry>();
            }

            var buffer = Marshal.AllocHGlobal(bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (!EnumServicesStatusEx(
                    handle,
                    ScEnumProcessInfo,
                    ServiceWin32,
                    ServiceStateAll,
                    buffer,
                    bytesNeeded,
                    out _,
                    out servicesReturned,
                    ref resumeHandle,
                    null))
                {
                    return Array.Empty<WindowsServiceEntry>();
                }

                var rowSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                var services = new List<WindowsServiceEntry>(servicesReturned);
                for (var i = 0; i < servicesReturned; i++)
                {
                    var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(nint.Add(buffer, i * rowSize));
                    var details = ReadServiceRegistryDetails(item.ServiceName);
                    services.Add(new WindowsServiceEntry(
                        item.ServiceName,
                        string.IsNullOrWhiteSpace(item.DisplayName) ? item.ServiceName : item.DisplayName,
                        FormatServiceState(item.Status.CurrentState),
                        details.StartType,
                        details.Path,
                        unchecked((int)item.Status.ProcessId)));
                }

                return services
                    .Where(s =>
                        s.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.Status.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.StartType.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || s.ProcessIdText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Status == "运行中")
                    .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(240)
                    .ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseServiceHandle(handle);
        }
    }

    private static (string StartType, string Path) ReadServiceRegistryDetails(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null)
            {
                return ("未知", string.Empty);
            }

            var start = key.GetValue("Start") is int value ? value : -1;
            var path = key.GetValue("ImagePath")?.ToString() ?? string.Empty;
            return (FormatServiceStartType(start), path);
        }
        catch
        {
            return ("未知", string.Empty);
        }
    }

    private static void AddDefaultProxy(List<ProxyEntry> entries, string probeUrl)
    {
        try
        {
            var probe = new Uri(probeUrl);
            var proxyUri = HttpClient.DefaultProxy.GetProxy(probe);
            if (proxyUri is not null && proxyUri != probe)
            {
                entries.Add(new ProxyEntry(probe.Scheme.ToUpperInvariant(), proxyUri.ToString(), "系统默认代理"));
            }
        }
        catch
        {
            // Proxy discovery can throw on broken machine policy. Treat it as no proxy.
        }
    }

    private static ProxyEntry? ReadRegistryProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                return null;
            }

            var enabled = key.GetValue("ProxyEnable") is int value && value != 0;
            var server = key.GetValue("ProxyServer")?.ToString() ?? string.Empty;
            if (!enabled || string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            return new ProxyEntry("WinINET", server, "系统代理");
        }
        catch
        {
            return null;
        }
    }

    private static string FormatIp(uint address)
        => new IPAddress(address).ToString();

    private static int ConvertPort(uint port)
        => (ushort)IPAddress.NetworkToHostOrder((short)port);

    private static string FormatTcpState(uint state)
        => state switch
        {
            1 => "关闭",
            2 => "监听",
            3 => "SYN 已发送",
            4 => "SYN 已接收",
            5 => "已建立",
            6 => "等待关闭",
            7 => "FIN 等待 1",
            8 => "关闭等待",
            9 => "FIN 等待 2",
            10 => "最后 ACK",
            11 => "TIME_WAIT",
            12 => "删除中",
            _ => "未知"
        };

    private static string FormatServiceState(uint state)
        => state switch
        {
            1 => "已停止",
            2 => "启动中",
            3 => "停止中",
            4 => "运行中",
            5 => "继续中",
            6 => "暂停中",
            7 => "已暂停",
            _ => "未知"
        };

    private static string FormatServiceStartType(int start)
        => start switch
        {
            0 => "引导",
            1 => "系统",
            2 => "自动",
            3 => "手动",
            4 => "禁用",
            _ => "未知"
        };

    private static bool IsUserVisibleAdapter(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return false;
        }

        var name = adapter.Name ?? string.Empty;
        var description = adapter.Description ?? string.Empty;
        var probe = name + " " + description;

        // Windows exposes filter/protocol drivers as NetworkInterface entries; ipconfig
        // does not show these as first-class adapters, so hide them in the default list.
        string[] hiddenDriverMarkers =
        [
            "WFP ",
            "Npcap",
            "QoS Packet Scheduler",
            "Native WiFi Filter Driver",
            "Virtual WiFi Filter Driver",
            "LightWeight Filter"
        ];

        if (hiddenDriverMarkers.Any(m => probe.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string[] hiddenAdapterMarkers =
        [
            "WAN Miniport",
            "Teredo",
            "6to4 Adapter",
            "IP-HTTPS",
            "Kernel Debug",
            "RAS Async Adapter",
            "VMware Virtual Ethernet Adapter"
        ];

        if (hiddenAdapterMarkers.Any(m => probe.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var properties = adapter.GetIPProperties();
        if (adapter.OperationalStatus == OperationalStatus.NotPresent
            && properties.UnicastAddresses.Count == 0)
        {
            return false;
        }

        return adapter.OperationalStatus == OperationalStatus.Up
            || properties.UnicastAddresses.Count > 0
            || IsKnownUserFacingDisconnectedAdapter(probe);
    }

    private static bool IsKnownUserFacingDisconnectedAdapter(string value)
    {
        string[] visibleDisconnectedMarkers =
        [
            "Wi-Fi Direct",
            "WiFi Direct",
            "Bluetooth Device",
            "TAP-Windows",
            "TAP-Win32",
            "Apple Mobile Device Ethernet",
            "USB Ethernet",
            "Ethernet Adapter"
        ];

        return visibleDisconnectedMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadCpuName()
    {
        try
        {
            return Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null) as string
                ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? "未知";
        }
        catch
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知";
        }
    }

    private static (string Total, string Available) ReadMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX();
        status.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();

        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return (FormatBytes((long)status.ullTotalPhys), FormatBytes((long)status.ullAvailPhys));
        }

        return ("未知", "未知");
    }

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "未知";
        }

        var mbps = bitsPerSecond / 1_000_000d;
        return mbps >= 1000
            ? (mbps / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps"
            : mbps.ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";
    }

    internal static string FormatBytes(long bytes)
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

    private const int AfInet = 2;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const int ServiceWin32 = 0x00000030;
    private const int ServiceStateAll = 0x00000003;

    private enum TcpTableClass
    {
        OwnerPidAll = 5
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ServiceName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DisplayName;

        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        TcpTableClass tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        nint pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        UdpTableClass tblClass,
        uint reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        nint serviceManager,
        int infoLevel,
        int serviceType,
        int serviceState,
        nint services,
        int bufferSize,
        out int bytesNeeded,
        out int servicesReturned,
        ref int resumeHandle,
        string? groupName);
}

public sealed record ToolActionResult(bool Success, string Message);

public sealed record NetworkSnapshot(
    DateTime RefreshedAt,
    bool IsNetworkAvailable,
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    string PublicIp,
    string PublicIpStatus)
{
    public string SummaryText
        => $"网络{(IsNetworkAvailable ? "可用" : "不可用")} · {Adapters.Count(a => a.IsUp)} 个活动网卡 · {RefreshedAt:HH:mm:ss}";
}

public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    string Type,
    string Status,
    bool IsUp,
    string Speed,
    string Addresses,
    string Ipv4,
    string Ipv6,
    string Gateways,
    string Dns)
{
    public string StatusText => $"{Status} · {Speed}";

    public string Ipv4Text => string.IsNullOrWhiteSpace(Ipv4) ? "无" : Ipv4;

    public string Ipv6Text => string.IsNullOrWhiteSpace(Ipv6) ? "无" : Ipv6;

    public string GatewayText => string.IsNullOrWhiteSpace(Gateways) ? "无" : Gateways;

    public string DnsText => string.IsNullOrWhiteSpace(Dns) ? "无" : Dns;

    public string Ipv4Line => $"IPv4: {Ipv4Text}";

    public string Ipv6Line => $"IPv6: {Ipv6Text}";

    public string GatewayLine => $"网关: {GatewayText}";

    public string DnsLine => $"DNS: {DnsText}";

    public string ToReportText()
        => string.Join(Environment.NewLine, new[]
        {
            $"[{Name}] {StatusText}",
            $"描述: {Description}",
            $"类型: {Type}",
            $"IPv4: {Ipv4Text}",
            $"IPv6: {Ipv6Text}",
            $"网关: {GatewayText}",
            $"DNS: {DnsText}"
        });
}

public sealed record ProxySnapshot(DateTime RefreshedAt, IReadOnlyList<ProxyEntry> Entries)
{
    public string SummaryText => Entries.Count == 0 ? "未检测到代理" : $"{Entries.Count} 项代理配置";

    public string DetailText
        => Entries.Count == 0
            ? "系统代理 / 环境变量代理：未检测到"
            : string.Join(Environment.NewLine, Entries.Select(e => $"{e.Name}: {e.Value} ({e.Source})"));
}

public sealed record ProxyEntry(string Name, string Value, string Source);

public sealed record PingProbeResult(
    string Host,
    bool Success,
    long RoundtripTimeMs,
    string Address,
    string Status,
    string Error)
{
    public string DisplayText
        => Success
            ? $"{Host} · {Address} · {RoundtripTimeMs} ms"
            : $"{Host} · {Status}{(string.IsNullOrWhiteSpace(Error) ? string.Empty : " · " + Error)}";
}

public sealed record DnsProbeResult(
    string Host,
    bool Success,
    long ElapsedMs,
    IReadOnlyList<string> Addresses,
    string Error)
{
    public string DisplayText
        => Success
            ? $"{Host} · {ElapsedMs} ms · {string.Join(", ", Addresses)}"
            : $"{Host} · 解析失败 · {ElapsedMs} ms · {Error}";

    public string ToReportText()
        => "DNS 解析" + Environment.NewLine + DisplayText;
}

public sealed record TcpProbeResult(
    string Host,
    int Port,
    bool Success,
    long ElapsedMs,
    string Error)
{
    public string DisplayText
        => Success
            ? $"{Host}:{Port} · 已连接 · {ElapsedMs} ms"
            : $"{Host}:{Port} · 连接失败 · {ElapsedMs} ms · {Error}";

    public string ToReportText()
        => "TCP 连接测试" + Environment.NewLine + DisplayText;
}

public sealed record SystemSnapshot(
    DateTime RefreshedAt,
    string OsDescription,
    string MachineName,
    string UserName,
    TimeSpan Uptime,
    string Architecture,
    string DotnetVersion,
    string CpuName,
    int ProcessorCount,
    string TotalMemory,
    string AvailableMemory,
    IReadOnlyList<DriveSnapshot> Drives)
{
    public string UptimeText => $"{(int)Uptime.TotalDays} 天 {Uptime.Hours} 小时 {Uptime.Minutes} 分钟";

    public string MemoryText => $"可用 {AvailableMemory} / 总计 {TotalMemory}";
}

public sealed record DriveSnapshot(string Name, string Total, string Free, string Format)
{
    public string FreeText => $"可用 {Free}";

    public string TotalText => $"总计 {Total}";
}

public sealed record PortEntry(
    string Protocol,
    string LocalAddress,
    int Port,
    string State,
    string RemoteAddress,
    int ProcessId,
    string ProcessName,
    string ProcessPath)
{
    public string Endpoint => $"{LocalAddress}:{Port}";

    public string PortLabel
        => CommonPortLabels.TryGetValue(Port, out var label) ? label : string.Empty;

    public string EndpointTitle
        => string.IsNullOrWhiteSpace(PortLabel) ? Endpoint : $"{Endpoint} · {PortLabel}";

    public string RemoteEndpointText => string.IsNullOrWhiteSpace(RemoteAddress) ? "无远端" : RemoteAddress;

    public string ProcessIdText => ProcessId <= 0 ? "不可用" : ProcessId.ToString(CultureInfo.InvariantCulture);

    public string ProcessDirectory
        => string.IsNullOrWhiteSpace(ProcessPath) ? string.Empty : Path.GetDirectoryName(ProcessPath) ?? string.Empty;

    public string Detail
        => string.IsNullOrWhiteSpace(PortLabel)
            ? $"{State} · PID {ProcessIdText} · {ProcessName} · {RemoteEndpointText}"
            : $"{State} · {PortLabel} · PID {ProcessIdText} · {ProcessName} · {RemoteEndpointText}";

    public bool IsListening => string.Equals(State, "监听", StringComparison.OrdinalIgnoreCase);

    public bool IsEstablished => string.Equals(State, "已建立", StringComparison.OrdinalIgnoreCase);

    public bool CanKill => ProcessId > 0 && ProcessId != Environment.ProcessId;

    public bool CanOpenDirectory => !string.IsNullOrWhiteSpace(ProcessDirectory);

    public bool CanOpenLocalhost
        => string.Equals(Protocol, "TCP", StringComparison.OrdinalIgnoreCase)
            && IsListening;

    public string LocalhostUrl => $"http://localhost:{Port}/";

    public string ProcessGroupTitle => $"{ProcessName} · PID {ProcessIdText}";

    public string ToReportText()
        => $"{Protocol} {Endpoint} {PortLabel} {State} 远端 {RemoteEndpointText} PID {ProcessIdText} {ProcessName} {ProcessPath}".TrimEnd();

    private static readonly IReadOnlyDictionary<int, string> CommonPortLabels = new Dictionary<int, string>
    {
        [80] = "HTTP",
        [443] = "HTTPS",
        [3000] = "Node / React",
        [3306] = "MySQL",
        [5000] = ".NET / Flask",
        [5173] = "Vite",
        [5432] = "PostgreSQL",
        [6379] = "Redis",
        [8000] = "Dev Server",
        [8080] = "HTTP Dev",
        [9000] = "Dev Server",
        [1433] = "SQL Server",
        [27017] = "MongoDB"
    };
}

public sealed record PortProcessGroup(
    string ProcessName,
    int ProcessId,
    string ProcessPath,
    int Count,
    string PortsText)
{
    public string ProcessIdText => ProcessId <= 0 ? "不可用" : ProcessId.ToString(CultureInfo.InvariantCulture);

    public string Title => $"{ProcessName} · PID {ProcessIdText}";

    public string Summary => $"{Count} 个端口";
}

public sealed record StartupServiceSnapshot(
    DateTime RefreshedAt,
    IReadOnlyList<StartupItemEntry> StartupItems,
    IReadOnlyList<WindowsServiceEntry> Services)
{
    public string SummaryText => $"{StartupItems.Count} 个启动项 · {Services.Count} 个服务 · {RefreshedAt:HH:mm:ss}";
}

public sealed record StartupItemEntry(
    string Name,
    string Source,
    string Command,
    string Location)
{
    public string Detail => $"{Source} · {Location}";
}

public sealed record WindowsServiceEntry(
    string ServiceName,
    string DisplayName,
    string Status,
    string StartType,
    string Path,
    int ProcessId)
{
    public string ProcessIdText => ProcessId <= 0 ? "不可用" : ProcessId.ToString(CultureInfo.InvariantCulture);

    public string Detail => $"{ServiceName} · {Status} · {StartType} · PID {ProcessIdText}";
}

public sealed record KillProcessResult(bool Success, string Message);
