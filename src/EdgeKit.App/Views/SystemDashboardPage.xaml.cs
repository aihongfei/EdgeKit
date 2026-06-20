using EdgeKit.App.ViewModels;
using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EdgeKit.App.Views;

public sealed partial class SystemDashboardPage : Page
{
    private SystemDashboardViewModel? _viewModel;
    private readonly DispatcherQueueTimer _timer;
    private bool _sampling;

    public SystemDashboardPage()
    {
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += OnTimerTick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not SystemDashboardPageParameter parameter)
        {
            return;
        }

        _viewModel = parameter.ViewModel;
        DataContext = _viewModel;
        _sampling = true;
        SamplingButtonText.Text = "暂停";
        CaptureAndRender();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer.Stop();
        _sampling = false;
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_sampling)
        {
            CaptureAndRender();
        }
    }

    private void OnSamplingClick(object sender, RoutedEventArgs e)
    {
        _sampling = !_sampling;
        SamplingButtonText.Text = _sampling ? "暂停" : "继续";
        StatusText.Text = _sampling ? "采样中" : "已暂停采样";
        if (_sampling)
        {
            CaptureAndRender();
        }
    }

    private void CaptureAndRender()
    {
        if (_viewModel is null)
        {
            return;
        }

        var snapshot = _viewModel.Capture();
        RenderSnapshot(snapshot);
    }

    private void RenderSnapshot(SystemResourceSnapshot snapshot)
    {
        CpuText.Text = FormatPercent(snapshot.CpuPercent);
        CpuBar.Value = snapshot.CpuPercent;

        MemoryText.Text = FormatPercent(snapshot.MemoryPercent);
        MemoryDetailText.Text = "已用 " + snapshot.MemoryUsedText + " / 可用 " + snapshot.MemoryAvailableText;
        MemoryBar.Value = snapshot.MemoryPercent;

        DiskText.Text = FormatPercent(snapshot.DiskPercent);
        DiskDetailText.Text = "已用 " + snapshot.DiskUsedText + " / 可用 " + snapshot.DiskFreeText;
        DiskBar.Value = snapshot.DiskPercent;

        NetworkText.Text = "↓ " + snapshot.NetworkReceiveText + "  ↑ " + snapshot.NetworkSendText;
        NetworkDetailText.Text = "实时上下行速率";
        StatusText.Text = "更新于 " + snapshot.CapturedAt.ToString("HH:mm:ss");
    }

    private static string FormatPercent(double value)
        => Math.Round(value, 1).ToString("0.#") + "%";
}