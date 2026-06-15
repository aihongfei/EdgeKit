using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace EdgeKit.App.Views;

/// <summary>诊断型系统工具工作台。</summary>
public sealed partial class SystemToolsPage : Page
{
    private readonly ObservableCollection<NetworkAdapterInfo> _networkAdapters = new();
    private readonly ObservableCollection<DriveSnapshot> _systemDrives = new();
    private readonly ObservableCollection<PortEntry> _ports = new();
    private readonly ObservableCollection<PortProcessGroup> _portGroups = new();
    private readonly ObservableCollection<EnvironmentVariableEntry> _environmentVariables = new();
    private readonly ObservableCollection<EnvironmentPathItem> _environmentPathItems = new();
    private readonly ObservableCollection<ManagedWindowEntry> _windows = new();
    private readonly ObservableCollection<FileLockEntry> _fileLocks = new();

    private readonly DispatcherQueueTimer _toastTimer;

    private SystemToolsPageParameter? _parameter;
    private NetworkSnapshot? _networkSnapshot;
    private ProxySnapshot? _proxySnapshot;
    private SystemSnapshot? _systemSnapshot;
    private HostsFileSnapshot? _hostsSnapshot;
    private EnvironmentVariableSnapshot? _environmentSnapshot;
    private WindowManagementSnapshot? _windowSnapshot;
    private FileLockSnapshot? _fileLockSnapshot;
    private EnvironmentVariableEntry? _selectedEnvironmentVariable;
    private ManagedWindowEntry? _selectedWindow;
    private PingProbeResult? _lastPingResult;
    private DnsProbeResult? _lastDnsResult;
    private TcpProbeResult? _lastTcpResult;
    private IReadOnlyList<PortEntry> _allPorts = Array.Empty<PortEntry>();
    private IReadOnlyList<EnvironmentVariableEntry> _allEnvironmentVariables = Array.Empty<EnvironmentVariableEntry>();
    private IReadOnlyList<ManagedWindowEntry> _allWindows = Array.Empty<ManagedWindowEntry>();
    private IReadOnlyList<FileLockEntry> _allFileLocks = Array.Empty<FileLockEntry>();
    private string _selectedTab = "network";
    private int _loadVersion;
    private bool _networkLoaded;
    private bool _portsLoaded;
    private bool _hostsLoaded;
    private bool _environmentLoaded;
    private bool _windowsLoaded;
    private bool _fileLocksLoaded;
    private bool _systemLoaded;
    private bool _loadingHostsEditor;
    private bool _syncingEnvironmentEditor;
    private bool _syncingWindowSelection;
    private bool _syncingFileLockSelection;

    public SystemToolsPage()
    {
        InitializeComponent();

        NetworkAdaptersList.ItemsSource = _networkAdapters;
        SystemDrivesGrid.ItemsSource = _systemDrives;
        PortsList.ItemsSource = _ports;
        PortGroupsList.ItemsSource = _portGroups;
        EnvironmentVariablesList.ItemsSource = _environmentVariables;
        EnvironmentPathItemsList.ItemsSource = _environmentPathItems;
        WindowsList.ItemsSource = _windows;
        FileLocksList.ItemsSource = _fileLocks;

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(2);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not SystemToolsPageParameter parameter)
        {
            return;
        }

        _parameter = parameter;
        SelectTab(ResolveInitialTab(parameter.ToolId));
        await LoadSelectedAsync(forceRefresh: false);
    }

    private static string ResolveInitialTab(string toolId)
    {
        if (toolId.Contains("port", StringComparison.OrdinalIgnoreCase))
        {
            return "ports";
        }

        if (toolId.Contains("host", StringComparison.OrdinalIgnoreCase))
        {
            return "hosts";
        }

        if (toolId.Contains("env", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("environment", StringComparison.OrdinalIgnoreCase))
        {
            return "env";
        }

        if (toolId.Contains("window", StringComparison.OrdinalIgnoreCase))
        {
            return "windows";
        }

        if (toolId.Contains("filelock", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("lock", StringComparison.OrdinalIgnoreCase))
        {
            return "filelock";
        }

        if (toolId.Contains("system", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        return "network";
    }

    private async void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
        {
            SelectTab(tab);
            await LoadSelectedAsync(forceRefresh: false);
        }
    }

    private void SelectTab(string tab)
    {
        _selectedTab = tab;

        NetworkPanel.Visibility = tab == "network" ? Visibility.Visible : Visibility.Collapsed;
        PortsPanel.Visibility = tab == "ports" ? Visibility.Visible : Visibility.Collapsed;
        HostsPanel.Visibility = tab == "hosts" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentPanel.Visibility = tab == "env" ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = tab == "windows" ? Visibility.Visible : Visibility.Collapsed;
        FileLockPanel.Visibility = tab == "filelock" ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = tab == "system" ? Visibility.Visible : Visibility.Collapsed;

        ApplyTabButtonState(NetworkTabButton, tab == "network");
        ApplyTabButtonState(PortsTabButton, tab == "ports");
        ApplyTabButtonState(HostsTabButton, tab == "hosts");
        ApplyTabButtonState(EnvironmentTabButton, tab == "env");
        ApplyTabButtonState(WindowTabButton, tab == "windows");
        ApplyTabButtonState(FileLockTabButton, tab == "filelock");
        ApplyTabButtonState(SystemTabButton, tab == "system");

        PageSubtitle.Text = tab switch
        {
            "ports" => "端口筛选、进程分组、复制信息和确认结束占用进程",
            "hosts" => "读取、备份、编辑和保存 hosts",
            "env" => "用户变量、系统变量、Path 列表编辑",
            "windows" => "窗口列表、置顶、透明度和会话内还原",
            "filelock" => "检测文件或文件夹占用进程，并提供回收站删除和高级强制处理",
            "system" => "系统版本、硬件、内存、磁盘",
            _ => "网卡、IP、代理、Ping、DNS 和 TCP 连接测试"
        };
    }

    private static void ApplyTabButtonState(Button button, bool selected)
    {
        button.Background = (Brush)Application.Current.Resources[
            selected ? "EdgeAccentSoftBrush" : "EdgeControlBrush"];
        button.Foreground = (Brush)Application.Current.Resources[
            selected ? "EdgeAccentBrush" : "EdgeTextBrush"];
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await LoadSelectedAsync(forceRefresh: true);

    private async Task LoadSelectedAsync(bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        var version = ++_loadVersion;
        var tab = _selectedTab;

        try
        {
            switch (tab)
            {
                case "ports":
                    await LoadPortsAsync(version, forceRefresh);
                    break;
                case "hosts":
                    await LoadHostsAsync(version, forceRefresh);
                    break;
                case "env":
                    await LoadEnvironmentAsync(version, forceRefresh);
                    break;
                case "windows":
                    await LoadWindowsAsync(version, forceRefresh);
                    break;
                case "filelock":
                    await LoadFileLocksAsync(version, forceRefresh);
                    break;
                case "system":
                    await LoadSystemAsync(version, forceRefresh);
                    break;
                default:
                    await LoadNetworkAsync(
                        version,
                        forceRefresh,
                        _networkSnapshot?.PublicIp,
                        _networkSnapshot?.PublicIpStatus);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentLoad(version, tab))
            {
                ShowToast("加载失败：" + ex.Message);
            }
        }
    }

    private async Task LoadNetworkAsync(
        int version,
        bool forceRefresh,
        string? publicIp = null,
        string? publicIpStatus = null)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _networkLoaded && _networkSnapshot is not null)
        {
            RenderNetwork();
            return;
        }

        SetNetworkLoadingState();
        var diagnostics = _parameter.Diagnostics;
        var data = await Task.Run(() => new NetworkLoadResult(
            diagnostics.GetNetworkSnapshot(publicIp, publicIpStatus),
            diagnostics.GetProxySnapshot()));
        if (!IsCurrentLoad(version, "network"))
        {
            return;
        }

        _networkSnapshot = data.Network;
        _proxySnapshot = data.Proxy;
        _networkLoaded = true;
        RenderNetwork();
    }

    private void RenderNetwork()
    {
        if (_networkSnapshot is null)
        {
            return;
        }

        NetworkSummaryText.Text = _networkSnapshot.SummaryText;
        PublicIpText.Text = "公网 IP: "
            + (string.IsNullOrWhiteSpace(_networkSnapshot.PublicIp)
                ? _networkSnapshot.PublicIpStatus
                : _networkSnapshot.PublicIp);
        ProxySummaryText.Text = _proxySnapshot?.DetailText ?? "代理信息未加载";

        _networkAdapters.Clear();
        foreach (var adapter in _networkSnapshot.Adapters)
        {
            _networkAdapters.Add(adapter);
        }
    }

    private async void OnRefreshNetworkClick(object sender, RoutedEventArgs e)
        => await LoadNetworkAsync(
            ++_loadVersion,
            forceRefresh: true,
            _networkSnapshot?.PublicIp,
            _networkSnapshot?.PublicIpStatus);

    private async void OnRefreshPublicIpClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var version = ++_loadVersion;
        SetNetworkLoadingState("公网 IP 正在刷新...");

        try
        {
            var ip = await _parameter.Diagnostics.GetPublicIpAsync();
            if (!IsCurrentLoad(version, "network"))
            {
                return;
            }

            await LoadNetworkAsync(version, forceRefresh: true, ip, "已刷新");
            if (IsCurrentLoad(version, "network"))
            {
                ShowToast("公网 IP 已刷新");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (!IsCurrentLoad(version, "network"))
            {
                return;
            }

            await LoadNetworkAsync(version, forceRefresh: true, string.Empty, "公网 IP 获取失败或超时");
            if (IsCurrentLoad(version, "network"))
            {
                ShowToast("公网 IP 获取失败");
            }
        }
    }

    private async void OnPingClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        PingStatusText.Text = "Ping 中...";
        _lastPingResult = await _parameter.Diagnostics.PingAsync(PingHostInput.Text);
        PingStatusText.Text = _lastPingResult.DisplayText;
        ShowToast(_lastPingResult.Success ? "Ping 成功" : "Ping 失败");
    }

    private async void OnDnsProbeClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        DnsStatusText.Text = "DNS 解析中...";
        _lastDnsResult = await _parameter.Diagnostics.ResolveDnsAsync(DnsHostInput.Text);
        DnsStatusText.Text = _lastDnsResult.DisplayText;
        ShowToast(_lastDnsResult.Success ? "DNS 解析成功" : "DNS 解析失败");
    }

    private async void OnTcpProbeClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        _ = int.TryParse(TcpPortInput.Text, out var port);
        TcpStatusText.Text = "TCP 连接测试中...";
        _lastTcpResult = await _parameter.Diagnostics.ProbeTcpAsync(TcpHostInput.Text, port);
        TcpStatusText.Text = _lastTcpResult.DisplayText;
        ShowToast(_lastTcpResult.Success ? "TCP 已连接" : "TCP 连接失败");
    }

    private void OnCopyPingResultClick(object sender, RoutedEventArgs e)
        => CopyProbeText(_lastPingResult?.DisplayText, "Ping 结果已复制");

    private void OnCopyDnsResultClick(object sender, RoutedEventArgs e)
        => CopyProbeText(_lastDnsResult?.ToReportText(), "DNS 结果已复制");

    private void OnCopyTcpResultClick(object sender, RoutedEventArgs e)
        => CopyProbeText(_lastTcpResult?.ToReportText(), "TCP 结果已复制");

    private void OnCopyNetworkClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _networkSnapshot is null)
        {
            return;
        }

        CopyText(_parameter.Diagnostics.BuildNetworkReport(_networkSnapshot, _proxySnapshot));
        ShowToast("网络信息已复制");
    }

    private void OnCopyAdapterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NetworkAdapterInfo adapter })
        {
            CopyText(adapter.ToReportText());
            ShowToast("网卡信息已复制");
        }
    }

    private async Task LoadPortsAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _portsLoaded)
        {
            RefreshPorts();
            return;
        }

        SetPortsLoadingState();
        var diagnostics = _parameter.Diagnostics;
        var ports = await Task.Run(diagnostics.GetPortEntries);
        if (!IsCurrentLoad(version, "ports"))
        {
            return;
        }

        _allPorts = ports;
        _portsLoaded = true;
        RefreshPorts();
    }

    private void OnPortSearchTextChanged(object sender, TextChangedEventArgs e)
        => RefreshPorts();

    private void OnPortFilterChanged(object sender, RoutedEventArgs e)
        => RefreshPorts();

    private void OnPortComboFilterChanged(object sender, SelectionChangedEventArgs e)
        => RefreshPorts();

    private void RefreshPorts()
    {
        if (PortSearchInput is null
            || PortProtocolFilter is null
            || PortStateFilter is null
            || GroupPortsCheckBox is null
            || PortsList is null
            || PortGroupsList is null
            || PortHeaderText is null)
        {
            return;
        }

        var source = GetFilteredPorts().ToArray();
        var grouped = GroupPortsCheckBox.IsChecked == true;

        _ports.Clear();
        _portGroups.Clear();

        if (grouped)
        {
            foreach (var group in source
                .GroupBy(p => new { p.ProcessId, p.ProcessName, p.ProcessPath })
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .Take(80))
            {
                _portGroups.Add(new PortProcessGroup(
                    group.Key.ProcessName,
                    group.Key.ProcessId,
                    group.Key.ProcessPath,
                    group.Count(),
                    string.Join("  ", group.Take(24).Select(p => $"{p.Protocol} {p.EndpointTitle} {p.State}"))));
            }
        }
        else
        {
            foreach (var port in source.Take(160))
            {
                _ports.Add(port);
            }
        }

        PortsList.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible;
        PortGroupsList.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        PortHeaderText.Text = grouped
            ? $"端口占用 · {_portGroups.Count} 组 / {source.Length} 条 / {_allPorts.Count} 总数"
            : $"端口占用 · {_ports.Count}/{source.Length} 条 / {_allPorts.Count} 总数";
    }

    private IEnumerable<PortEntry> GetFilteredPorts()
    {
        var query = PortSearchInput.Text?.Trim() ?? string.Empty;
        var protocol = GetComboText(PortProtocolFilter);
        var state = GetComboText(PortStateFilter);

        return _allPorts.Where(p =>
        {
            if (!string.Equals(protocol, "全部", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(state, "监听", StringComparison.OrdinalIgnoreCase) && !p.IsListening)
            {
                return false;
            }

            if (string.Equals(state, "已建立", StringComparison.OrdinalIgnoreCase) && !p.IsEstablished)
            {
                return false;
            }

            if (string.Equals(state, "其他", StringComparison.OrdinalIgnoreCase)
                && (p.IsListening || p.IsEstablished))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(query)
                || p.Port.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.LocalAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.RemoteAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.PortLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.ProcessIdText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.ProcessPath.Contains(query, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async void OnKillPortClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || sender is not Button { Tag: PortEntry entry })
        {
            return;
        }

        if (!entry.CanKill)
        {
            ShowToast("该端口没有可结束的进程");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "结束占用端口的进程",
            Content = $"确定结束 {entry.ProcessName} (PID {entry.ProcessId})？\n端口：{entry.Protocol} {entry.Endpoint}",
            PrimaryButtonText = "结束进程",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = _parameter.Diagnostics.KillPortOwner(entry);
        if (result.Success)
        {
            await Task.Delay(500);
            await LoadPortsAsync(++_loadVersion, forceRefresh: true);
            var released = !_allPorts.Any(p =>
                p.Port == entry.Port
                && string.Equals(p.Protocol, entry.Protocol, StringComparison.OrdinalIgnoreCase)
                && p.ProcessId == entry.ProcessId);
            ShowToast(result.Message + (released ? "，端口已释放" : "，请稍后刷新确认"));
            return;
        }

        ShowToast(result.Message);
    }

    private void OnCopyPortClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PortEntry entry })
        {
            CopyText(entry.ToReportText());
            ShowToast("端口信息已复制");
        }
    }

    private void OnOpenPortProcessFolderClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || sender is not Button { Tag: PortEntry entry })
        {
            return;
        }

        var result = _parameter.Diagnostics.OpenProcessDirectory(entry);
        ShowToast(result.Message);
    }

    private void OnOpenPortLocalhostClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || sender is not Button { Tag: PortEntry entry })
        {
            return;
        }

        var result = _parameter.Diagnostics.OpenPortLocalhost(entry);
        ShowToast(result.Message);
    }

    private void OnCopyPortsClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        CopyText(_parameter.Diagnostics.BuildPortReport(GetFilteredPorts().ToArray()));
        ShowToast("端口列表已复制");
    }

    private async Task LoadFileLocksAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        var path = FileLockPathInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            FileLockSummaryText.Text = "输入文件或文件夹路径后开始检测";
            FileLockStatusText.Text = "可从命令面板打开本页，也可以直接粘贴路径。";
            _fileLocks.Clear();
            _allFileLocks = Array.Empty<FileLockEntry>();
            _fileLockSnapshot = null;
            _fileLocksLoaded = false;
            RefreshFileLockSelectionText();
            return;
        }

        if (!forceRefresh && _fileLocksLoaded && _fileLockSnapshot is not null)
        {
            RenderFileLocks();
            return;
        }

        SetFileLocksLoadingState();
        var snapshot = await _parameter.FileLocks.ScanAsync(path);
        if (!IsCurrentLoad(version, "filelock"))
        {
            return;
        }

        foreach (var entry in snapshot.Entries)
        {
            entry.IsSelected = entry.CanKill || entry.CanCloseHandle;
        }

        _fileLockSnapshot = snapshot;
        _allFileLocks = snapshot.Entries;
        _fileLocksLoaded = true;
        RenderFileLocks();
    }

    private void RenderFileLocks()
    {
        if (_fileLockSnapshot is null)
        {
            return;
        }

        FileLockSummaryText.Text = _fileLockSnapshot.SummaryText;
        FileLockStatusText.Text = string.IsNullOrWhiteSpace(_fileLockSnapshot.Warning)
            ? _fileLockSnapshot.TargetPath
            : _fileLockSnapshot.TargetPath + Environment.NewLine + _fileLockSnapshot.Warning;

        _fileLocks.Clear();
        foreach (var entry in _allFileLocks)
        {
            _fileLocks.Add(entry);
        }

        UpdateFileLockSelectAllState();
        RefreshFileLockSelectionText();
    }

    private async void OnPickFileLockFileClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var path = ShellPathPicker.PickFilePath(_parameter.OwnerHwnd);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FileLockPathInput.Text = path;
        await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);
    }

    private async void OnPickFileLockFolderClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var path = ShellPathPicker.PickFolderPath(_parameter.OwnerHwnd);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FileLockPathInput.Text = path;
        await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);
    }

    private async void OnScanFileLocksClick(object sender, RoutedEventArgs e)
        => await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);

    private void OnFileLockSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateFileLockSelectAllState();
        RefreshFileLockSelectionText();
    }

    private void OnSelectAllFileLocksChanged(object sender, RoutedEventArgs e)
    {
        if (SelectAllFileLocksCheckBox is null)
        {
            return;
        }

        if (_syncingFileLockSelection)
        {
            return;
        }

        var selected = SelectAllFileLocksCheckBox.IsChecked == true;
        foreach (var entry in _fileLocks)
        {
            entry.IsSelected = selected;
        }

        FileLocksList.ItemsSource = null;
        FileLocksList.ItemsSource = _fileLocks;
        RefreshFileLockSelectionText();
    }

    private async void OnDeleteFileLockTargetClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || !TryGetFileLockTarget(out var path, out var isDirectory))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除到回收站",
            Content = $"确定将目标移入回收站？\n{path}",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.FileLocks.DeleteToRecycleBinAsync(path, isDirectory);
        ShowToast(result.Message);
        if (result.Success)
        {
            _fileLocksLoaded = false;
            await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);
        }
    }

    private async void OnKillFileLockProcessesClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || !TryGetFileLockTarget(out var path, out var isDirectory))
        {
            return;
        }

        var entries = GetSelectedFileLocks().Where(e => e.CanKill).ToArray();
        if (entries.Length == 0)
        {
            ShowToast("请选择可结束的占用项");
            return;
        }

        var processCount = entries.Select(e => e.ProcessId).Distinct().Count();
        var dialog = new ContentDialog
        {
            Title = "结束进程后删除",
            Content = $"将结束 {processCount} 个进程，然后把目标移入回收站。\n未保存的数据可能丢失。\n{path}",
            PrimaryButtonText = "结束并删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.FileLocks.KillProcessesAndDeleteAsync(path, isDirectory, entries);
        ShowToast(result.Message);
        await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);
    }

    private async void OnCloseFileLockHandlesClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || !TryGetFileLockTarget(out var path, out var isDirectory))
        {
            return;
        }

        var entries = GetSelectedFileLocks().Where(e => e.CanCloseHandle).ToArray();
        if (entries.Length == 0)
        {
            ShowToast("请选择可关闭的文件句柄");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "关闭句柄后删除",
            Content = $"将关闭 {entries.Length} 个远程文件句柄，然后把目标移入回收站。\n这个操作可能导致占用程序出错或数据损坏。\n{path}",
            PrimaryButtonText = "关闭并删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.FileLocks.CloseHandlesAndDeleteAsync(path, isDirectory, entries);
        ShowToast(result.Message);
        await LoadFileLocksAsync(++_loadVersion, forceRefresh: true);
    }

    private void OnCopyFileLockReportClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _fileLockSnapshot is null)
        {
            ShowToast("没有可复制的检测报告");
            return;
        }

        CopyText(_parameter.FileLocks.BuildReport(_fileLockSnapshot));
        ShowToast("文件锁定报告已复制");
    }

    private void OnCopyFileLockEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileLockEntry entry })
        {
            CopyText(entry.ToReportText());
            ShowToast("占用信息已复制");
        }
    }

    private void OnOpenFileLockProcessFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FileLockEntry entry })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.ProcessDirectory)
            || !System.IO.Directory.Exists(entry.ProcessDirectory))
        {
            ShowToast("进程目录不可用");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.ProcessDirectory)
            {
                UseShellExecute = true
            });
            ShowToast("已打开进程目录");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowToast("打开失败：" + ex.Message);
        }
    }

    private bool TryGetFileLockTarget(out string path, out bool isDirectory)
    {
        path = FileLockPathInput.Text?.Trim().Trim('"') ?? string.Empty;
        isDirectory = _fileLockSnapshot?.IsDirectory ?? System.IO.Directory.Exists(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowToast("请输入文件或文件夹路径");
            return false;
        }

        return true;
    }

    private FileLockEntry[] GetSelectedFileLocks()
        => _fileLocks.Where(e => e.IsSelected).ToArray();

    private void RefreshFileLockSelectionText()
    {
        if (FileLockSelectionText is null)
        {
            return;
        }

        FileLockSelectionText.Text = _fileLocks.Count == 0
            ? string.Empty
            : $"{_fileLocks.Count(e => e.IsSelected)}/{_fileLocks.Count} 项已选";
    }

    private void UpdateFileLockSelectAllState()
    {
        if (SelectAllFileLocksCheckBox is null)
        {
            return;
        }

        _syncingFileLockSelection = true;
        SelectAllFileLocksCheckBox.IsChecked = _fileLocks.Count > 0 && _fileLocks.All(e => e.IsSelected);
        _syncingFileLockSelection = false;
    }

    private async Task LoadHostsAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _hostsLoaded && _hostsSnapshot is not null)
        {
            RenderHosts();
            return;
        }

        SetHostsLoadingState();
        var snapshot = await Task.Run(_parameter.Hosts.ReadSnapshot);
        if (!IsCurrentLoad(version, "hosts"))
        {
            return;
        }

        _hostsSnapshot = snapshot;
        _hostsLoaded = true;
        RenderHosts();
    }

    private void RenderHosts()
    {
        if (_hostsSnapshot is null)
        {
            return;
        }

        HostsSummaryText.Text = _hostsSnapshot.SummaryText;
        HostsPathText.Text = _hostsSnapshot.Path;
        _loadingHostsEditor = true;
        HostsEditor.Text = _hostsSnapshot.Content;
        _loadingHostsEditor = false;
        RefreshHostsSummary();
    }

    private void OnHostsEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingHostsEditor)
        {
            RefreshHostsSummary();
        }
    }

    private void RefreshHostsSummary()
    {
        if (_parameter is null)
        {
            return;
        }

        var content = HostsEditor.Text ?? string.Empty;
        var snapshot = _parameter.Hosts.BuildSnapshot(content);
        var entryCount = snapshot.Rows.Count(r => r.IsEntry);
        HostsSummaryText.Text = $"{entryCount} 条记录 · {(snapshot.IsAdministrator ? "管理员" : "普通权限")} · {snapshot.RefreshedAt:HH:mm:ss}";
        HostsPathText.Text = snapshot.Path;
    }

    private async void OnSaveHostsClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "保存 hosts",
            Content = "保存前会自动备份当前 hosts。普通权限下会弹出管理员授权。",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.Hosts.SaveAsync(HostsEditor.Text ?? string.Empty);
        if (result.Success)
        {
            var flush = _parameter.Hosts.FlushDns();
            ShowToast(result.Message + "；" + flush.Message);
            await LoadHostsAsync(++_loadVersion, forceRefresh: true);
            return;
        }

        ShowToast(result.Message);
    }

    private async void OnRestoreHostsBackupClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var backups = (_hostsSnapshot?.Backups.Count > 0
                ? _hostsSnapshot.Backups
                : _parameter.Hosts.ReadSnapshot().Backups)
            .ToArray();
        if (backups.Length == 0)
        {
            ShowToast("没有可恢复的 hosts 备份");
            return;
        }

        var backupPicker = new ComboBox
        {
            ItemsSource = backups,
            DisplayMemberPath = nameof(HostsBackupInfo.DisplayText),
            SelectedIndex = 0,
            MinWidth = 360
        };

        var dialog = new ContentDialog
        {
            Title = "恢复 hosts 备份",
            Content = backupPicker,
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (backupPicker.SelectedItem is not HostsBackupInfo backup)
        {
            ShowToast("请选择一个备份");
            return;
        }

        var result = await _parameter.Hosts.RestoreBackupAsync(backup.Path);
        if (result.Success)
        {
            var flush = _parameter.Hosts.FlushDns();
            ShowToast(result.Message + "；" + flush.Message);
            await LoadHostsAsync(++_loadVersion, forceRefresh: true);
            return;
        }

        ShowToast(result.Message);
    }

    private void OnFlushDnsClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        ShowToast(_parameter.Hosts.FlushDns().Message);
    }

    private void OnCopyHostsContentClick(object sender, RoutedEventArgs e)
    {
        CopyText(HostsEditor.Text ?? string.Empty);
        ShowToast("hosts 内容已复制");
    }

    private async Task LoadEnvironmentAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _environmentLoaded && _environmentSnapshot is not null)
        {
            RenderEnvironment();
            return;
        }

        SetEnvironmentLoadingState();
        var snapshot = await Task.Run(_parameter.EnvironmentVariables.ReadSnapshot);
        if (!IsCurrentLoad(version, "env"))
        {
            return;
        }

        _environmentSnapshot = snapshot;
        _allEnvironmentVariables = snapshot.Entries;
        _environmentLoaded = true;
        RenderEnvironment();
    }

    private void RenderEnvironment()
    {
        if (_environmentSnapshot is null)
        {
            return;
        }

        EnvironmentSummaryText.Text = _environmentSnapshot.SummaryText;
        RefreshEnvironmentVariables();
        if (_selectedEnvironmentVariable is null && _environmentVariables.Count > 0)
        {
            EnvironmentVariablesList.SelectedIndex = 0;
        }
        else
        {
            RenderEnvironmentEditor(_selectedEnvironmentVariable);
        }
    }

    private void OnEnvironmentFilterChanged(object sender, TextChangedEventArgs e)
        => RefreshEnvironmentVariables();

    private void OnEnvironmentScopeChanged(object sender, SelectionChangedEventArgs e)
        => RefreshEnvironmentVariables();

    private void RefreshEnvironmentVariables()
    {
        if (EnvironmentSearchInput is null || EnvironmentScopeFilter is null)
        {
            return;
        }

        var query = EnvironmentSearchInput.Text?.Trim() ?? string.Empty;
        var scope = GetComboText(EnvironmentScopeFilter);
        var source = _allEnvironmentVariables.Where(entry =>
        {
            if (string.Equals(scope, "用户变量", StringComparison.OrdinalIgnoreCase)
                && entry.Target != EnvironmentVariableTarget.User)
            {
                return false;
            }

            if (string.Equals(scope, "系统变量", StringComparison.OrdinalIgnoreCase)
                && entry.Target != EnvironmentVariableTarget.Machine)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(query)
                || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Value.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.TargetText.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        _environmentVariables.Clear();
        foreach (var entry in source.Take(240))
        {
            _environmentVariables.Add(entry);
        }

        if (_environmentSnapshot is not null)
        {
            EnvironmentSummaryText.Text = $"{_environmentVariables.Count}/{source.Length} 个变量 · {_environmentSnapshot.SummaryText}";
        }
    }

    private void OnEnvironmentVariableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedEnvironmentVariable = EnvironmentVariablesList.SelectedItem as EnvironmentVariableEntry;
        RenderEnvironmentEditor(_selectedEnvironmentVariable);
    }

    private void RenderEnvironmentEditor(EnvironmentVariableEntry? entry)
    {
        _syncingEnvironmentEditor = true;
        if (entry is null)
        {
            EnvironmentEditorHeaderText.Text = "新建变量";
            EnvironmentNameInput.Text = string.Empty;
            EnvironmentTargetInput.SelectedIndex = 0;
            EnvironmentValueInput.Text = string.Empty;
            EnvironmentEditorStatusText.Text = "输入变量名和值后保存";
        }
        else
        {
            EnvironmentEditorHeaderText.Text = entry.IsPathVariable ? "编辑 Path" : "编辑变量";
            EnvironmentNameInput.Text = entry.Name;
            EnvironmentTargetInput.SelectedIndex = entry.Target == EnvironmentVariableTarget.Machine ? 1 : 0;
            EnvironmentValueInput.Text = entry.Value;
            EnvironmentEditorStatusText.Text = $"{entry.TargetText}变量 · {entry.Value.Length} 个字符";
        }

        _syncingEnvironmentEditor = false;
        RefreshEnvironmentPathEditor();
    }

    private void OnNewEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        _selectedEnvironmentVariable = null;
        EnvironmentVariablesList.SelectedItem = null;
        RenderEnvironmentEditor(null);
    }

    private void OnEnvironmentNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingEnvironmentEditor)
        {
            RefreshEnvironmentPathEditor();
        }
    }

    private void OnEnvironmentValueChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingEnvironmentEditor)
        {
            RefreshEnvironmentPathEditor();
        }
    }

    private void RefreshEnvironmentPathEditor()
    {
        if (_parameter is null || EnvironmentPathPanel is null)
        {
            return;
        }

        if (!string.Equals(EnvironmentNameInput.Text?.Trim(), "Path", StringComparison.OrdinalIgnoreCase))
        {
            EnvironmentPathPanel.Visibility = Visibility.Collapsed;
            _environmentPathItems.Clear();
            return;
        }

        EnvironmentPathPanel.Visibility = Visibility.Visible;
        var items = _parameter.EnvironmentVariables.AnalyzePath(EnvironmentValueInput.Text ?? string.Empty);
        _environmentPathItems.Clear();
        foreach (var item in items)
        {
            _environmentPathItems.Add(item);
        }

        var issueCount = items.Count(i => i.IssueText != "正常");
        EnvironmentPathIssuesText.Text = issueCount == 0
            ? $"{items.Count} 项 · 未发现问题"
            : $"{items.Count} 项 · {issueCount} 项需要注意";
    }

    private async void OnSaveEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var target = GetEnvironmentEditorTarget();
        var name = EnvironmentNameInput.Text?.Trim() ?? string.Empty;
        var value = EnvironmentValueInput.Text ?? string.Empty;
        var dialog = new ContentDialog
        {
            Title = "保存环境变量",
            Content = target == EnvironmentVariableTarget.Machine
                ? "保存系统变量前会自动备份当前系统变量，并弹出管理员授权。保存后新进程生效。"
                : "保存用户变量前会自动备份当前用户变量。保存后新进程生效。",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.EnvironmentVariables.SaveAsync(target, name, value);
        ShowToast(result.Message);
        if (result.Success)
        {
            await LoadEnvironmentAsync(++_loadVersion, forceRefresh: true);
            SelectEnvironmentVariable(name, target);
        }
    }

    private async void OnDeleteEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var target = GetEnvironmentEditorTarget();
        var name = EnvironmentNameInput.Text?.Trim() ?? string.Empty;
        var dialog = new ContentDialog
        {
            Title = "删除环境变量",
            Content = $"确定删除 {GetEnvironmentTargetText(target)}变量 {name}？删除前会自动备份。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await _parameter.EnvironmentVariables.DeleteAsync(target, name);
        ShowToast(result.Message);
        if (result.Success)
        {
            _selectedEnvironmentVariable = null;
            await LoadEnvironmentAsync(++_loadVersion, forceRefresh: true);
            RenderEnvironmentEditor(null);
        }
    }

    private void OnCopyEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        var name = EnvironmentNameInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("没有可复制的变量");
            return;
        }

        CopyText($"{name}={EnvironmentValueInput.Text ?? string.Empty}");
        ShowToast("环境变量已复制");
    }

    private void OnCopyEnvironmentListClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        CopyText(_parameter.EnvironmentVariables.BuildReport(_environmentVariables.ToArray()));
        ShowToast("环境变量列表已复制");
    }

    private void OnAddEnvironmentPathItemClick(object sender, RoutedEventArgs e)
    {
        var value = EnvironmentPathNewItemInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowToast("请输入路径项");
            return;
        }

        var values = _environmentPathItems.Select(i => i.Value).ToList();
        values.Add(value);
        EnvironmentPathNewItemInput.Text = string.Empty;
        SetEnvironmentPathValues(values, values.Count - 1);
    }

    private void OnRemoveEnvironmentPathItemClick(object sender, RoutedEventArgs e)
    {
        var index = EnvironmentPathItemsList.SelectedIndex;
        if (index < 0 || index >= _environmentPathItems.Count)
        {
            ShowToast("请选择路径项");
            return;
        }

        var values = _environmentPathItems.Select(i => i.Value).ToList();
        values.RemoveAt(index);
        SetEnvironmentPathValues(values, Math.Min(index, values.Count - 1));
    }

    private void OnMoveEnvironmentPathItemUpClick(object sender, RoutedEventArgs e)
        => MoveEnvironmentPathItem(-1);

    private void OnMoveEnvironmentPathItemDownClick(object sender, RoutedEventArgs e)
        => MoveEnvironmentPathItem(1);

    private void MoveEnvironmentPathItem(int direction)
    {
        var index = EnvironmentPathItemsList.SelectedIndex;
        var next = index + direction;
        if (index < 0 || next < 0 || next >= _environmentPathItems.Count)
        {
            return;
        }

        var values = _environmentPathItems.Select(i => i.Value).ToList();
        (values[index], values[next]) = (values[next], values[index]);
        SetEnvironmentPathValues(values, next);
    }

    private void OnDedupeEnvironmentPathClick(object sender, RoutedEventArgs e)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = _environmentPathItems
            .Select(i => i.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Where(v => seen.Add(v.TrimEnd('\\', '/')))
            .ToList();
        SetEnvironmentPathValues(values, values.Count > 0 ? 0 : -1);
        ShowToast("Path 空项和重复项已移除");
    }

    private void SetEnvironmentPathValues(IReadOnlyList<string> values, int selectedIndex)
    {
        _syncingEnvironmentEditor = true;
        EnvironmentValueInput.Text = string.Join(";", values);
        _syncingEnvironmentEditor = false;
        RefreshEnvironmentPathEditor();
        if (selectedIndex >= 0 && selectedIndex < _environmentPathItems.Count)
        {
            EnvironmentPathItemsList.SelectedIndex = selectedIndex;
        }
    }

    private void SelectEnvironmentVariable(string name, EnvironmentVariableTarget target)
    {
        var match = _environmentVariables.FirstOrDefault(e =>
            e.Target == target && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            EnvironmentVariablesList.SelectedItem = match;
        }
    }

    private EnvironmentVariableTarget GetEnvironmentEditorTarget()
        => EnvironmentTargetInput.SelectedIndex == 1
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

    private static string GetEnvironmentTargetText(EnvironmentVariableTarget target)
        => target == EnvironmentVariableTarget.Machine ? "系统" : "用户";

    private async Task LoadWindowsAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _windowsLoaded && _windowSnapshot is not null)
        {
            RenderWindows();
            return;
        }

        SetWindowsLoadingState();
        var owner = _parameter.OwnerHwnd;
        var snapshot = await Task.Run(() => _parameter.WindowManagement.ReadSnapshot(owner));
        if (!IsCurrentLoad(version, "windows"))
        {
            return;
        }

        _windowSnapshot = snapshot;
        _allWindows = snapshot.Windows;
        _windowsLoaded = true;
        RenderWindows();
    }

    private void RenderWindows()
    {
        if (_windowSnapshot is null)
        {
            return;
        }

        WindowSummaryText.Text = _windowSnapshot.SummaryText;
        RefreshWindows();
        if (_selectedWindow is null && _windows.Count > 0)
        {
            WindowsList.SelectedIndex = 0;
        }
        else
        {
            RenderSelectedWindow(_selectedWindow);
        }
    }

    private void OnWindowSearchChanged(object sender, TextChangedEventArgs e)
        => RefreshWindows();

    private void RefreshWindows()
    {
        if (WindowSearchInput is null)
        {
            return;
        }

        var query = WindowSearchInput.Text?.Trim() ?? string.Empty;
        var source = _allWindows.Where(window =>
            string.IsNullOrWhiteSpace(query)
            || window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || window.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || window.ProcessIdText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || window.ProcessPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _windows.Clear();
        foreach (var window in source.Take(180))
        {
            _windows.Add(window);
        }

        if (_windowSnapshot is not null)
        {
            WindowSummaryText.Text = $"{_windows.Count}/{source.Length} 个窗口 · {_windowSnapshot.RefreshedAt:HH:mm:ss}";
        }
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedWindow = WindowsList.SelectedItem as ManagedWindowEntry;
        RenderSelectedWindow(_selectedWindow);
    }

    private void RenderSelectedWindow(ManagedWindowEntry? entry)
    {
        _syncingWindowSelection = true;
        if (entry is null)
        {
            WindowTitleText.Text = "选择窗口";
            WindowProcessText.Text = string.Empty;
            WindowPathText.Text = string.Empty;
            WindowStateText.Text = string.Empty;
            WindowOpacitySlider.Value = 100;
            WindowOpacityText.Text = string.Empty;
            _syncingWindowSelection = false;
            return;
        }

        WindowTitleText.Text = entry.Title;
        WindowProcessText.Text = $"{entry.ProcessName} · PID {entry.ProcessIdText} · {entry.HwndHex}";
        WindowPathText.Text = entry.ProcessPath;
        WindowStateText.Text = $"矩形: {entry.RectText} · 置顶: {entry.TopMostText} · 透明度: {entry.OpacityText}";
        WindowOpacitySlider.Value = entry.OpacityPercent;
        WindowOpacityText.Text = entry.OpacityText;
        _syncingWindowSelection = false;
    }

    private async void OnRefreshWindowsClick(object sender, RoutedEventArgs e)
        => await LoadWindowsAsync(++_loadVersion, forceRefresh: true);

    private async void OnSetWindowTopMostClick(object sender, RoutedEventArgs e)
        => await ApplyWindowActionAsync(window => _parameter!.WindowManagement.SetTopMost(window, true));

    private async void OnUnsetWindowTopMostClick(object sender, RoutedEventArgs e)
        => await ApplyWindowActionAsync(window => _parameter!.WindowManagement.SetTopMost(window, false));

    private async void OnRestoreWindowClick(object sender, RoutedEventArgs e)
        => await ApplyWindowActionAsync(window => _parameter!.WindowManagement.RestoreWindow(window));

    private void OnActivateWindowClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _selectedWindow is null)
        {
            ShowToast("请选择窗口");
            return;
        }

        ShowToast(_parameter.WindowManagement.ActivateWindow(_selectedWindow).Message);
    }

    private void OnOpenWindowProcessDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _selectedWindow is null)
        {
            ShowToast("请选择窗口");
            return;
        }

        ShowToast(_parameter.WindowManagement.OpenProcessDirectory(_selectedWindow).Message);
    }

    private void OnCopyWindowInfoClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _selectedWindow is null)
        {
            ShowToast("请选择窗口");
            return;
        }

        CopyText(_parameter.WindowManagement.BuildWindowReport(_selectedWindow));
        ShowToast("窗口信息已复制");
    }

    private void OnWindowOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingWindowSelection || _parameter is null || _selectedWindow is null)
        {
            return;
        }

        var opacity = (int)Math.Round(e.NewValue);
        WindowOpacityText.Text = opacity + "%";
        var result = _parameter.WindowManagement.SetOpacity(_selectedWindow, opacity);
        if (!result.Success)
        {
            ShowToast(result.Message);
            return;
        }

        _selectedWindow = _selectedWindow with { OpacityPercent = opacity };
        WindowStateText.Text = $"矩形: {_selectedWindow.RectText} · 置顶: {_selectedWindow.TopMostText} · 透明度: {_selectedWindow.OpacityText}";
    }

    private async Task ApplyWindowActionAsync(Func<ManagedWindowEntry, ToolActionResult> action)
    {
        if (_parameter is null || _selectedWindow is null)
        {
            ShowToast("请选择窗口");
            return;
        }

        var hwnd = _selectedWindow.Hwnd;
        var result = action(_selectedWindow);
        ShowToast(result.Message);
        if (result.Success)
        {
            await LoadWindowsAsync(++_loadVersion, forceRefresh: true);
            SelectWindow(hwnd);
        }
    }

    private void SelectWindow(nint hwnd)
    {
        var match = _windows.FirstOrDefault(w => w.Hwnd == hwnd);
        if (match is not null)
        {
            WindowsList.SelectedItem = match;
        }
    }

    private async Task LoadSystemAsync(int version, bool forceRefresh)
    {
        if (_parameter is null)
        {
            return;
        }

        if (!forceRefresh && _systemLoaded && _systemSnapshot is not null)
        {
            RenderSystem();
            return;
        }

        SetSystemLoadingState();
        var diagnostics = _parameter.Diagnostics;
        var snapshot = await Task.Run(diagnostics.GetSystemSnapshot);
        if (!IsCurrentLoad(version, "system"))
        {
            return;
        }

        _systemSnapshot = snapshot;
        _systemLoaded = true;
        RenderSystem();
    }

    private void RenderSystem()
    {
        if (_systemSnapshot is null)
        {
            return;
        }

        SnapshotSummaryText.Text = $"{_systemSnapshot.MachineName} · {_systemSnapshot.UserName} · {_systemSnapshot.RefreshedAt:HH:mm:ss}";
        OsText.Text = $"系统: {_systemSnapshot.OsDescription}";
        MachineText.Text = $"开机时长: {_systemSnapshot.UptimeText} · 架构: {_systemSnapshot.Architecture} · .NET: {_systemSnapshot.DotnetVersion}";
        CpuText.Text = $"CPU: {_systemSnapshot.CpuName} ({_systemSnapshot.ProcessorCount} 线程)";
        MemoryText.Text = $"内存: {_systemSnapshot.MemoryText}";
        SystemCompactSummaryText.Text = _parameter?.Diagnostics.BuildSystemSummary(_systemSnapshot) ?? string.Empty;

        _systemDrives.Clear();
        foreach (var drive in _systemSnapshot.Drives)
        {
            _systemDrives.Add(drive);
        }
    }

    private bool IsCurrentLoad(int version, string tab)
        => version == _loadVersion && string.Equals(_selectedTab, tab, StringComparison.OrdinalIgnoreCase);

    private void SetNetworkLoadingState(string? message = null)
    {
        NetworkSummaryText.Text = message
            ?? (_networkLoaded ? "网络诊断刷新中..." : "网络诊断加载中...");
        PublicIpText.Text = _networkSnapshot is null
            ? "公网 IP: 未刷新"
            : "公网 IP: " + (string.IsNullOrWhiteSpace(_networkSnapshot.PublicIp)
                ? _networkSnapshot.PublicIpStatus
                : _networkSnapshot.PublicIp);
        ProxySummaryText.Text = _proxySnapshot?.DetailText ?? "代理信息加载中...";

        if (!_networkLoaded)
        {
            _networkAdapters.Clear();
        }
    }

    private void SetPortsLoadingState()
    {
        PortHeaderText.Text = _portsLoaded ? "端口占用 · 刷新中..." : "端口占用 · 加载中...";
        if (!_portsLoaded)
        {
            _ports.Clear();
            _portGroups.Clear();
        }
    }

    private void SetHostsLoadingState()
    {
        HostsSummaryText.Text = _hostsLoaded ? "Hosts 刷新中..." : "Hosts 加载中...";
        HostsPathText.Text = string.Empty;
        if (!_hostsLoaded)
        {
            _loadingHostsEditor = true;
            HostsEditor.Text = string.Empty;
            _loadingHostsEditor = false;
        }
    }

    private void SetEnvironmentLoadingState()
    {
        EnvironmentSummaryText.Text = _environmentLoaded ? "环境变量刷新中..." : "环境变量加载中...";
        if (!_environmentLoaded)
        {
            _environmentVariables.Clear();
            _environmentPathItems.Clear();
            _syncingEnvironmentEditor = true;
            EnvironmentNameInput.Text = string.Empty;
            EnvironmentValueInput.Text = string.Empty;
            _syncingEnvironmentEditor = false;
            EnvironmentEditorStatusText.Text = string.Empty;
            EnvironmentPathPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SetWindowsLoadingState()
    {
        WindowSummaryText.Text = _windowsLoaded ? "窗口列表刷新中..." : "窗口列表加载中...";
        if (!_windowsLoaded)
        {
            _windows.Clear();
            RenderSelectedWindow(null);
        }
    }

    private void SetFileLocksLoadingState()
    {
        FileLockSummaryText.Text = _fileLocksLoaded ? "文件锁定检测刷新中..." : "文件锁定检测中...";
        FileLockStatusText.Text = "正在枚举系统文件句柄...";
        if (!_fileLocksLoaded)
        {
            _fileLocks.Clear();
            _allFileLocks = Array.Empty<FileLockEntry>();
            RefreshFileLockSelectionText();
        }
    }

    private void SetSystemLoadingState()
    {
        SnapshotSummaryText.Text = _systemLoaded ? "系统信息刷新中..." : "系统信息加载中...";
        if (!_systemLoaded)
        {
            OsText.Text = string.Empty;
            MachineText.Text = string.Empty;
            CpuText.Text = string.Empty;
            MemoryText.Text = string.Empty;
            SystemCompactSummaryText.Text = string.Empty;
            _systemDrives.Clear();
        }
    }

    private void OnCopySystemSummaryClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _systemSnapshot is null)
        {
            return;
        }

        CopyText(_parameter.Diagnostics.BuildSystemSummary(_systemSnapshot));
        ShowToast("系统摘要已复制");
    }

    private void OnCopySnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null || _systemSnapshot is null)
        {
            return;
        }

        CopyText(_parameter.Diagnostics.BuildSystemReport(_systemSnapshot));
        ShowToast("系统报告已复制");
    }

    private void OnCopyTaggedTextClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || value == "无")
        {
            ShowToast("没有可复制的内容");
            return;
        }

        CopyText(value);
        ShowToast("已复制");
    }

    private void CopyProbeText(string? text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("没有可复制的结果");
            return;
        }

        CopyText(text);
        ShowToast(message);
    }

    private static string GetComboText(ComboBox? comboBox)
        => (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private sealed record NetworkLoadResult(NetworkSnapshot Network, ProxySnapshot Proxy);
}
