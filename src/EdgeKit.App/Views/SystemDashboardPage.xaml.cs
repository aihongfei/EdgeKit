using System.Threading.Tasks;
using EdgeKit.App.ViewModels;
using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Syncfusion.UI.Xaml.Charts;
using Windows.UI;

namespace EdgeKit.App.Views;

public sealed partial class SystemDashboardPage : Page
{
    private SystemDashboardViewModel? _viewModel;
    private readonly DispatcherQueueTimer _timer;
    private bool _sampling;
    private bool _captureInProgress;
    private int _captureGeneration;

    private readonly Dictionary<string, SplineAreaSeries> _diskSeries = new();

    private static readonly string[] DiskChartColors =
    [
        "#FFAA00",
        "#FFCC00",
        "#FFDD44",
        "#FFEE88",
        "#FFC107",
        "#FFB300",
        "#FFA000",
        "#FF8F00"
    ];

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
        SyncDiskSeries(snapshot);

        NetworkReceiveText.Text = snapshot.NetworkReceiveText;
        NetworkSendText.Text = snapshot.NetworkSendText;
        StatusText.Text = "更新于 " + snapshot.CapturedAt.ToString("HH:mm:ss");
    }

    private void SyncDiskSeries(SystemResourceSnapshot snapshot)
    {
        if (_viewModel is null)
        {
            return;
        }

        var currentLetters = snapshot.DiskDetails.Select(d => d.Letter).OrderBy(l => l).ToList();
        var existingLetters = _diskSeries.Keys.ToList();

        foreach (var letter in existingLetters.Except(currentLetters))
        {
            if (_diskSeries.TryGetValue(letter, out var series))
            {
                ResourceChart.Series.Remove(series);
                _diskSeries.Remove(letter);
            }
        }

        var networkSeries = ResourceChart.Series.OfType<SplineAreaSeries>()
            .FirstOrDefault(s => s.Label is "下行" or "上行");
        var insertIndex = networkSeries is null
            ? ResourceChart.Series.Count
            : ResourceChart.Series.IndexOf(networkSeries);

        var newLetters = currentLetters.Except(existingLetters).OrderByDescending(l => l).ToList();

        foreach (var letter in newLetters)
        {
            if (!_viewModel.DiskHistory.TryGetValue(letter, out var history))
            {
                continue;
            }

            var colorIndex = currentLetters.IndexOf(letter);
            var color = ParseColor(DiskChartColors[colorIndex % DiskChartColors.Length]);
            var fillBrush = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(0, 1)
            };
            fillBrush.GradientStops.Add(new GradientStop
            {
                Offset = 0,
                Color = Color.FromArgb(0x66, color.R, color.G, color.B)
            });
            fillBrush.GradientStops.Add(new GradientStop
            {
                Offset = 1,
                Color = Color.FromArgb(0x00, color.R, color.G, color.B)
            });

            var series = new SplineAreaSeries
            {
                Label = letter,
                ItemsSource = history,
                XBindingPath = "CapturedAt",
                YBindingPath = "UsedPercent",
                YAxisName = "PercentAxis",
                Stroke = new SolidColorBrush(color),
                Fill = fillBrush,
                StrokeWidth = 2,
                EnableTooltip = true
            };

            ResourceChart.Series.Insert(insertIndex, series);
            _diskSeries[letter] = series;
        }
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            0xFF,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
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