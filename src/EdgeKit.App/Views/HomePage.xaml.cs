using System;
using System.IO;
using EdgeKit.App.ViewModels;
using EdgeKit.App.Interaction;
using EdgeKit.Core.QuickLaunch;
using EdgeKit.Core.Recent;
using EdgeKit.Services.Quotes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace EdgeKit.App.Views;

/// <summary>
/// 首页仪表盘页。展示活动窗口与最近使用工具两组卡片。
/// 点击窗口项尝试激活/重新拉起对应应用；点击工具项回调选中左侧菜单对应工具。
/// </summary>
public sealed partial class HomePage : Page
{
    private const double QuickLaunchTileHeight = 92;
    private const double RecentToolsTileHeight = 92;
    private static readonly TimeSpan ActiveWindowRefreshInterval = TimeSpan.FromSeconds(1);

    private HomeViewModel? _viewModel;
    private Action<string>? _selectTool;
    private Action? _focusSearch;
    private nint _windowHandle;
    private readonly DispatcherQueueTimer _copyToastTimer;
    private readonly DispatcherQueueTimer _activeWindowTimer;
    private HomeNavigationTarget? _focusedHomeTarget;

    public HomePage()
    {
        InitializeComponent();

        _copyToastTimer = DispatcherQueue.CreateTimer();
        _copyToastTimer.Interval = TimeSpan.FromSeconds(1.4);
        _copyToastTimer.IsRepeating = false;
        _copyToastTimer.Tick += OnCopyToastTimerTick;

        _activeWindowTimer = DispatcherQueue.CreateTimer();
        _activeWindowTimer.Interval = ActiveWindowRefreshInterval;
        _activeWindowTimer.IsRepeating = true;
        _activeWindowTimer.Tick += OnActiveWindowTimerTick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HomePageParameter parameter)
        {
            return;
        }

        _viewModel = parameter.ViewModel;
        _selectTool = parameter.SelectTool;
        _focusSearch = parameter.FocusSearch;
        _windowHandle = parameter.WindowHandle;

        // 每次进入重新拉取最新数据。
        _viewModel.MaterializeQuickLaunchIcons(DispatcherQueue);
        _viewModel.Refresh();
        _viewModel.RefreshActiveWindows(_windowHandle);

        QuickLaunchGrid.ItemsSource = _viewModel.QuickLaunchTiles;
        WindowsList.ItemsSource = _viewModel.ActiveWindows;
        ToolsList.ItemsSource = _viewModel.RecentTools;
        ClipboardList.ItemsSource = _viewModel.RecentClipboardItems;

        UpdateEmptyStates();

        // 页面可见期间订阅仓储变更，实时刷新最近使用、快速启动与剪贴板。
        _viewModel.HomeDataChanged += OnHomeDataChanged;
        _activeWindowTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // 离开页面时取消订阅，避免不可见时仍刷新与事件泄漏。
        if (_viewModel is not null)
        {
            _viewModel.HomeDataChanged -= OnHomeDataChanged;
        }

        _activeWindowTimer.Stop();
    }

    private void OnHomeDataChanged(object? sender, EventArgs e)
    {
        // 仓储变更可能在非 UI 线程触发，调度回 UI 线程刷新。
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewModel?.MaterializeQuickLaunchIcons(DispatcherQueue);
            _viewModel?.Refresh();
            UpdateEmptyStates();
        });
    }

    private void OnActiveWindowTimerTick(DispatcherQueueTimer sender, object args)
    {
        _viewModel?.RefreshActiveWindows(_windowHandle);
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        if (_viewModel is null)
        {
            return;
        }

        WindowsEmpty.Visibility = _viewModel.HasNoActiveWindows
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        WindowsList.Visibility = _viewModel.HasNoActiveWindows
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

        ToolsEmpty.Visibility = _viewModel.HasNoTools
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        ToolsList.Visibility = _viewModel.HasNoTools
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

        ClipboardSection.Visibility = _viewModel.ShowClipboardHistory
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        ClipboardEmpty.Visibility = _viewModel.HasNoClipboardItems
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        ClipboardList.Visibility = _viewModel.HasNoClipboardItems
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

        DailyQuoteSection.Visibility = _viewModel.ShowDailyQuote
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        UpdateDailyQuote();

        QuickLaunchGrid.MaxHeight = Math.Max(
            QuickLaunchTileHeight,
            _viewModel.QuickLaunchVisibleRows * QuickLaunchTileHeight);

        ToolsList.MaxHeight = Math.Max(
            RecentToolsTileHeight,
            _viewModel.RecentToolsVisibleRows * RecentToolsTileHeight);
    }

    private void UpdateDailyQuote()
    {
        if (_viewModel is null || !_viewModel.ShowDailyQuote)
        {
            return;
        }

        var quote = _viewModel.DailyQuote;
        if (quote is null)
        {
            DailyQuoteText.Text = string.Empty;
            DailyQuoteAttribution.Text = string.Empty;
            return;
        }

        DailyQuoteText.Text = quote.Content;
        DailyQuoteAttribution.Text = string.IsNullOrWhiteSpace(quote.AttributionText)
            ? $"— {quote.CategoryDisplayName}"
            : $"— {quote.AttributionText}";
    }

    private void OnRefreshDailyQuoteClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _viewModel?.RefreshDailyQuote();
        UpdateDailyQuote();
    }

    private void OnQuickLaunchItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickLaunchTileViewModel tile)
        {
            return;
        }

        var anchor = QuickLaunchGrid.ContainerFromItem(tile) as FrameworkElement ?? QuickLaunchGrid;
        ActivateQuickLaunchTile(tile, anchor);
    }

    private void ActivateQuickLaunchTile(QuickLaunchTileViewModel tile, FrameworkElement anchor)
    {
        if (tile.IsAddTile)
        {
            ShowAddQuickLaunchMenu(anchor);
            return;
        }

        _viewModel?.LaunchQuickLaunchItem(tile);
    }

    private void OnQuickLaunchTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QuickLaunchTileViewModel tile } anchor)
        {
            return;
        }

        if (tile.IsAddTile)
        {
            ShowAddQuickLaunchMenu(anchor, e.GetPosition(anchor));
            e.Handled = true;
            return;
        }

        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "打开" };
        open.Click += (_, _) => _viewModel?.LaunchQuickLaunchItem(tile);
        flyout.Items.Add(open);

        var location = new MenuFlyoutItem
        {
            Text = "打开所在位置",
            IsEnabled = _viewModel?.CanOpenQuickLaunchLocation(tile) == true
        };
        location.Click += (_, _) => _viewModel?.OpenQuickLaunchLocation(tile);
        flyout.Items.Add(location);

        var admin = new MenuFlyoutItem
        {
            Text = "以管理员方式打开",
            IsEnabled = _viewModel?.CanRunQuickLaunchAsAdmin(tile) == true
        };
        admin.Click += (_, _) => _viewModel?.RunQuickLaunchAsAdmin(tile);
        flyout.Items.Add(admin);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "移除" };
        remove.Click += (_, _) => _viewModel?.DeleteQuickLaunchItem(tile);
        flyout.Items.Add(remove);

        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = e.GetPosition(anchor) });
        e.Handled = true;
    }

    private void ShowAddQuickLaunchMenu(FrameworkElement anchor, global::Windows.Foundation.Point? position = null)
    {
        var flyout = new MenuFlyout();

        var addFile = new MenuFlyoutItem { Text = "添加应用或文件" };
        addFile.Click += async (_, _) => await AddQuickLaunchFileAsync();
        flyout.Items.Add(addFile);

        var addFolder = new MenuFlyoutItem { Text = "添加文件夹" };
        addFolder.Click += async (_, _) => await AddQuickLaunchFolderAsync();
        flyout.Items.Add(addFolder);

        if (position is { } point)
        {
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        }
        else
        {
            flyout.ShowAt(anchor);
        }
    }

    private async System.Threading.Tasks.Task AddQuickLaunchFileAsync()
    {
        if (_viewModel is null || _windowHandle == nint.Zero)
        {
            return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is null || string.IsNullOrWhiteSpace(file.Path))
        {
            return;
        }

        var kind = QuickLaunchActionService.IsExecutablePath(file.Path)
            ? QuickLaunchItemKind.App
            : QuickLaunchItemKind.File;
        _viewModel.AddQuickLaunchItem(kind, Path.GetFileName(file.Path), file.Path);
    }

    private async System.Threading.Tasks.Task AddQuickLaunchFolderAsync()
    {
        if (_viewModel is null || _windowHandle == nint.Zero)
        {
            return;
        }

        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrWhiteSpace(folder.Path))
        {
            return;
        }

        _viewModel.AddQuickLaunchItem(QuickLaunchItemKind.Folder, folder.Name, folder.Path);
    }

    private void OnWindowItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ManagedWindowViewModel vm)
        {
            ActivateWindowItem(vm);
        }
    }

    private void OnToolItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RecentItemViewModel vm)
        {
            return;
        }

        ActivateRecentUsageItem(vm);
    }

    private void ActivateRecentUsageItem(RecentItemViewModel vm)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (vm.Model.Kind == RecentItemKind.Tool)
        {
            // Key 即工具 Id，回调让抽屉选中对应菜单项并切换内容区。
            _selectTool?.Invoke(vm.Model.Key);
            return;
        }

        if (vm.Model.Kind == RecentItemKind.App)
        {
            _viewModel.LaunchRecentApp(vm.Model);
        }
    }

    private void ActivateWindowItem(ManagedWindowViewModel vm)
    {
        _viewModel?.ActivateActiveWindow(vm);
    }

    private void OnClipboardItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipboardItemViewModel vm)
        {
            CopyClipboardItem(vm);
        }
    }

    public bool FocusHomeCardFromSearch(VirtualKey key)
    {
        if (!IsHomeNavigationKey(key))
        {
            return false;
        }

        var targets = GetHomeNavigationTargets();
        if (targets.Count == 0)
        {
            return false;
        }

        FocusHomeTarget(targets[0]);
        return true;
    }

    private void OnHomeCardsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var target = GetFocusedHomeTarget() ?? _focusedHomeTarget;
            if (target is not null)
            {
                ActivateHomeTarget(target);
                e.Handled = true;
            }

            return;
        }

        if (!IsHomeNavigationKey(e.Key))
        {
            return;
        }

        var current = GetFocusedHomeTarget() ?? _focusedHomeTarget;
        if (current is null)
        {
            FocusHomeCardFromSearch(e.Key);
            e.Handled = true;
            return;
        }

        MoveHomeFocus(current, e.Key);
        e.Handled = true;
    }

    private void MoveHomeFocus(HomeNavigationTarget current, VirtualKey key)
    {
        var targets = GetHomeNavigationTargets();
        if (targets.Count == 0)
        {
            return;
        }

        var next = FindDirectionalTarget(current, key, targets);
        if (next is null && key == VirtualKey.Up)
        {
            _focusedHomeTarget = null;
            _focusSearch?.Invoke();
            return;
        }

        next ??= FindLinearTarget(current, key, targets);

        if (next is not null)
        {
            FocusHomeTarget(next);
        }
    }

    private HomeNavigationTarget? FindDirectionalTarget(
        HomeNavigationTarget current,
        VirtualKey key,
        IReadOnlyList<HomeNavigationTarget> targets)
    {
        var currentElement = GetTargetContainer(current);
        if (currentElement is null)
        {
            return null;
        }

        var currentCenter = GetElementCenter(currentElement);
        HomeNavigationTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var target in targets)
        {
            if (target.Equals(current))
            {
                continue;
            }

            var element = GetTargetContainer(target);
            if (element is null)
            {
                continue;
            }

            var center = GetElementCenter(element);
            var dx = center.X - currentCenter.X;
            var dy = center.Y - currentCenter.Y;
            var isInDirection = key switch
            {
                VirtualKey.Left => dx < -4,
                VirtualKey.Right => dx > 4,
                VirtualKey.Up => dy < -4,
                VirtualKey.Down => dy > 4,
                _ => false
            };

            if (!isInDirection)
            {
                continue;
            }

            var primary = key is VirtualKey.Left or VirtualKey.Right ? Math.Abs(dx) : Math.Abs(dy);
            var cross = key is VirtualKey.Left or VirtualKey.Right ? Math.Abs(dy) : Math.Abs(dx);
            var score = (primary * 2) + cross;
            if (score < bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        return best;
    }

    private static HomeNavigationTarget? FindLinearTarget(
        HomeNavigationTarget current,
        VirtualKey key,
        IReadOnlyList<HomeNavigationTarget> targets)
    {
        var currentIndex = -1;
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Equals(current))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return targets[0];
        }

        var delta = key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
        var nextIndex = (currentIndex + delta + targets.Count) % targets.Count;
        return targets[nextIndex];
    }

    private void FocusHomeTarget(HomeNavigationTarget target)
    {
        _focusedHomeTarget = target;
        var item = target.List.Items[target.Index];
        target.List.ScrollIntoView(item);

        DispatcherQueue.TryEnqueue(() =>
        {
            var container = GetTargetContainer(target);
            container?.StartBringIntoView();
            container?.Focus(FocusState.Programmatic);
        });
    }

    private HomeNavigationTarget? GetFocusedHomeTarget()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            foreach (var target in GetHomeNavigationTargets())
            {
                if (ReferenceEquals(GetTargetContainer(target), focused))
                {
                    _focusedHomeTarget = target;
                    return target;
                }
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return null;
    }

    private List<HomeNavigationTarget> GetHomeNavigationTargets()
    {
        var targets = new List<HomeNavigationTarget>();
        AddHomeNavigationTargets(targets, ToolsList);
        AddHomeNavigationTargets(targets, QuickLaunchGrid);
        AddHomeNavigationTargets(targets, WindowsList);
        AddHomeNavigationTargets(targets, ClipboardList);
        return targets;
    }

    private static void AddHomeNavigationTargets(List<HomeNavigationTarget> targets, ListViewBase list)
    {
        if (list.Visibility != Visibility.Visible)
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            targets.Add(new HomeNavigationTarget(list, i));
        }
    }

    private static Control? GetTargetContainer(HomeNavigationTarget target)
        => target.List.ContainerFromIndex(target.Index) as Control;

    private global::Windows.Foundation.Point GetElementCenter(FrameworkElement element)
    {
        var topLeft = element.TransformToVisual(this).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        return new global::Windows.Foundation.Point(
            topLeft.X + (element.ActualWidth / 2),
            topLeft.Y + (element.ActualHeight / 2));
    }

    private void ActivateHomeTarget(HomeNavigationTarget target)
    {
        var item = target.List.Items[target.Index];
        var anchor = GetTargetContainer(target) as FrameworkElement ?? target.List;

        if (target.List == ToolsList && item is RecentItemViewModel recentUsage)
        {
            ActivateRecentUsageItem(recentUsage);
            return;
        }

        if (target.List == QuickLaunchGrid && item is QuickLaunchTileViewModel quickLaunch)
        {
            ActivateQuickLaunchTile(quickLaunch, anchor);
            return;
        }

        if (target.List == WindowsList && item is ManagedWindowViewModel window)
        {
            ActivateWindowItem(window);
            return;
        }

        if (target.List == ClipboardList && item is ClipboardItemViewModel clipboard)
        {
            CopyClipboardItem(clipboard);
        }
    }

    private static bool IsHomeNavigationKey(VirtualKey key)
        => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down;

    private void CopyClipboardItem(ClipboardItemViewModel item)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            _viewModel.CopyClipboardItem(item);
            ShowCopyToast("已复制到剪贴板", success: true);
        }
        catch
        {
            ShowCopyToast("复制失败", success: false);
        }
    }

    private void ShowCopyToast(string text, bool success)
    {
        CopyToastText.Text = text;
        CopyToastIcon.Glyph = success ? "\uE73E" : "\uE711";
        CopyToastIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            success ? "EdgeAccentBrush" : "EdgeMutedBrush"];
        CopyToast.Visibility = Visibility.Visible;

        _copyToastTimer.Stop();
        _copyToastTimer.Start();
    }

    private void OnCopyToastTimerTick(DispatcherQueueTimer sender, object args)
    {
        CopyToast.Visibility = Visibility.Collapsed;
    }

    private sealed record HomeNavigationTarget(ListViewBase List, int Index);
}
