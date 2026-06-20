using System.Threading.Tasks;
using EdgeKit.App.ViewModels;
using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Syncfusion.UI.Xaml.Charts;

namespace EdgeKit.App.Views;

public sealed partial class SystemDashboardPage : Page
{
    private SystemDashboardViewModel? _viewModel;
    private readonly DispatcherQueueTimer _timer;
    private bool _sampling;
    private bool _captureInProgress;
    private int _captureGeneration;

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
        StatusText.Text = "正在采集系统资源...";

        _timer.Stop();
        _ = CaptureAndRenderAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _timer.Stop();
        _sampling = false;
        _captureGeneration++;
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_sampling)
        {
            _ = CaptureAndRenderAsync();
        }
    }

    private void OnSamplingClick(object sender, RoutedEventArgs e)
    {
        _sampling = !_sampling;
        SamplingButtonText.Text = _sampling ? "暂停" : "继续";
        StatusText.Text = _sampling ? "采样中" : "已暂停采样";
        if (_sampling)
        {
            _ = CaptureAndRenderAsync();
        }
    }

    private async Task CaptureAndRenderAsync()
    {
        if (_viewModel is null || _captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        var generation = _captureGeneration;

        try
        {
            var viewModel = _viewModel;
            var snapshot = await Task.Run(viewModel.CaptureSnapshot);

            if (generation != _captureGeneration || _viewModel != viewModel)
            {
                return;
            }

            viewModel.ApplySnapshot(snapshot);
            RenderSnapshot(snapshot);
            if (_sampling)
            {
                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "资源采集失败：" + ex.Message;
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void RenderSnapshot(SystemResourceSnapshot snapshot)
    {
        CpuText.Text = FormatPercent(snapshot.CpuPercent);
        CpuFrequencyText.Text = snapshot.CpuFrequencyGHz.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GHz";
        CpuBar.Value = snapshot.CpuPercent;

        MemoryText.Text = FormatPercent(snapshot.MemoryPercent);
        MemoryDetailText.Text = "已用 " + snapshot.MemoryUsedText + " / 可用 " + snapshot.MemoryAvailableText;
        MemoryBar.Value = snapshot.MemoryPercent;

        DiskText.Text = FormatPercent(snapshot.DiskPercent);
        DiskList.ItemsSource = snapshot.DiskDetails;

        NetworkReceiveText.Text = snapshot.NetworkReceiveText;
        NetworkSendText.Text = snapshot.NetworkSendText;
        StatusText.Text = "更新于 " + snapshot.CapturedAt.ToString("HH:mm:ss");
    }

    private void OnDateTimeAxisLabelCreated(object sender, ChartAxisLabelEventArgs e)
    {
        if (DateTime.TryParse(e.Label, out var capturedAt))
        {
            e.Label = capturedAt.ToString("HH:mm:ss");
            return;
        }

        e.Label = DateTime.FromOADate(e.Position).ToString("HH:mm:ss");
    }

    private static string FormatPercent(double value)
        => Math.Round(value, 1).ToString("0.#") + "%";
}