using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using EdgeKit.App.Interaction;
using EdgeKit.App.Search;
using EdgeKit.App.Views;
using EdgeKit.Core.Recent;
using EdgeKit.Core.QuickLaunch;
using EdgeKit.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace EdgeKit.App.Windows;

/// <summary>
/// DrawerWindow 顶部统一搜索：整框聚焦高亮、Ctrl+K 聚焦、下拉结果列表，
/// 同时检索本机已安装应用与软件内置工具，回车 / 点击执行启动或跳转。
/// </summary>
public sealed partial class DrawerWindow
{
    private readonly ObservableCollection<SearchItem> _searchResults = new();
    private const int MaxSearchInputLength = 2048;
    private CancellationTokenSource? _searchCancellationSource;
    private bool _isSearchContextMenuOpen;
    private bool _isNormalizingSearchInput;
    private bool _isUpdatingSearchSourceVisuals;

    /// <summary>初始化搜索列表数据源（构造函数内调用）。</summary>
    private void InitializeSearch()
    {
        SearchResults.ItemsSource = _searchResults;
        _searchCoordinator.EverythingStateChanged += OnEverythingStateChanged;
        UpdateSearchSourceVisuals();
    }

    private void OnEverythingStateChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateSearchSourceVisuals();
            return;
        }

        DispatcherQueue.TryEnqueue(UpdateSearchSourceVisuals);
    }

    // ---- 聚焦与整框高亮 ----

    private void OnSearchBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SearchInput.Focus(FocusState.Programmatic);
    }

    private void OnSearchAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (App.IsRecordingGlobalHotkey)
        {
            args.Handled = true;
            return;
        }

        FocusSearchInput();
        args.Handled = true;
    }

    private void OnHideAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (App.IsRecordingGlobalHotkey)
        {
            args.Handled = true;
            return;
        }

        ClearSearch();
        HideDrawer();
        args.Handled = true;
    }

    public void ShowDrawerAndFocusSearch()
    {
        StartSearchInputKeepOpen();
        ShowDrawer();

        if (_state == DrawerState.Shown)
        {
            FocusSearchInput();
            return;
        }

        _focusSearchTimer.Stop();
        _focusSearchTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, _settings.AnimationDurationMs + 40));
        _focusSearchTimer.Start();
    }

    private void OnFocusSearchTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        FocusSearchInput();
    }

    private void FocusSearchInput()
    {
        EnsureDrawerForeground();
        SearchInput.Focus(FocusState.Programmatic);
        SearchInput.SelectAll();
        if (IsDrawerForegroundWindow())
        {
            _searchKeepOpenSawDrawerForeground = true;
        }
    }

    private void ResetSearchFocusState()
    {
        StopSearchInputKeepOpen();
        _focusSearchTimer.Stop();
        SearchPopup.IsOpen = false;
        SearchBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeLineBrush"];
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void StartSearchInputKeepOpen()
    {
        _keepOpenForSearchInput = true;
        _searchKeepOpenSawDrawerForeground = false;
        _searchKeepOpenStartedTicks = Environment.TickCount64;
        _cursorOutsideSinceTicks = null;
    }

    private void StopSearchInputKeepOpen()
    {
        _keepOpenForSearchInput = false;
        _searchKeepOpenSawDrawerForeground = false;
    }

    private void EnsureDrawerForeground()
    {
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        SearchBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeAccentBrush"];
        if (_keepOpenForSearchInput && IsDrawerForegroundWindow())
        {
            _searchKeepOpenSawDrawerForeground = true;
        }

        if (_searchResults.Count > 0)
        {
            SearchPopup.IsOpen = true;
        }
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        SearchBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeLineBrush"];
        if (_isSearchContextMenuOpen)
        {
            return;
        }

        SearchPopup.IsOpen = false;
    }

    // ---- 搜索与结果填充 ----

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isNormalizingSearchInput)
        {
            return;
        }

        TryNormalizeSearchInputText();
        await RefreshSearchResultsAsync();
    }

    private async Task RefreshSearchResultsAsync()
    {
        _searchCancellationSource?.Cancel();
        _searchCancellationSource?.Dispose();
        _searchCancellationSource = new CancellationTokenSource();
        var cancellationToken = _searchCancellationSource.Token;

        var query = SearchInput.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearSearchResults();
            UpdateSearchSourceVisuals();
            return;
        }

        if (!_settings.UseEverythingSearch)
        {
            _appIndex.MaterializeIcons(DispatcherQueue);
        }

        IReadOnlyList<SearchItem> results;
        try
        {
            results = await _searchCoordinator.SearchAsync(query, _settings.UseEverythingSearch, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _searchResults.Clear();
        foreach (var item in results)
        {
            _searchResults.Add(item);
        }

        if (_searchResults.Count > 0)
        {
            SearchPopup.IsOpen = true;
            SearchResults.SelectedIndex = 0;
            SearchPopupRoot.Width = SearchBox.ActualWidth;
        }
        else
        {
            SearchPopup.IsOpen = false;
        }

        UpdateSearchSourceVisuals();
    }

    private bool TryNormalizeSearchInputText()
    {
        var text = SearchInput.Text;
        var normalized = NormalizeSearchInputText(text);
        if (normalized == text)
        {
            return false;
        }

        _isNormalizingSearchInput = true;
        SearchInput.Text = normalized;
        SearchInput.SelectionStart = SearchInput.Text.Length;
        _isNormalizingSearchInput = false;
        return true;
    }

    private static string NormalizeSearchInputText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxSearchInputLength));
        var previousWasSpace = true;
        foreach (var ch in text)
        {
            if (builder.Length >= MaxSearchInputLength)
            {
                break;
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    // ---- 键盘导航 ----

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                if (TryFocusHomeCardFromEmptySearch(e.Key))
                {
                    e.Handled = true;
                    break;
                }

                MoveSelection(1);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                if (TryFocusHomeCardFromEmptySearch(e.Key))
                {
                    e.Handled = true;
                    break;
                }

                MoveSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.Left:
            case VirtualKey.Right:
                if (TryFocusHomeCardFromEmptySearch(e.Key))
                {
                    e.Handled = true;
                }

                break;

            case VirtualKey.Enter:
                if (SearchResults.SelectedItem is SearchItem item)
                {
                    ExecuteSearchItem(item);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Escape:
                ClearSearch();
                HideDrawer();
                e.Handled = true;
                break;

            case VirtualKey.Tab:
                _ = ToggleEverythingSearchModeAsync();
                e.Handled = true;
                break;
        }
    }

    private bool TryFocusHomeCardFromEmptySearch(VirtualKey key)
    {
        if (!string.IsNullOrEmpty(SearchInput.Text) || _searchResults.Count > 0)
        {
            return false;
        }

        return ContentFrame.Content is HomePage homePage
            && homePage.FocusHomeCardFromSearch(key);
    }

    private void MoveSelection(int delta)
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        var next = SearchResults.SelectedIndex + delta;
        if (next < 0)
        {
            next = _searchResults.Count - 1;
        }
        else if (next >= _searchResults.Count)
        {
            next = 0;
        }

        SearchResults.SelectedIndex = next;
        SearchResults.ScrollIntoView(SearchResults.SelectedItem);
    }

    // ---- 执行 ----

    private void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItem item)
        {
            ExecuteSearchItem(item);
        }
    }

    private void OnSearchResultRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SearchItem item } anchor)
        {
            return;
        }

        SearchResults.SelectedItem = item;

        var flyout = BuildSearchResultFlyout(item);
        _isSearchContextMenuOpen = true;
        flyout.Closed += (_, _) => _isSearchContextMenuOpen = false;
        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = e.GetPosition(anchor) });
        e.Handled = true;
    }

    private MenuFlyout BuildSearchResultFlyout(SearchItem item)
    {
        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = item.Kind == SearchItemKind.Command ? "执行" : "打开" };
        open.Click += (_, _) => ExecuteSearchItem(item);
        flyout.Items.Add(open);

        if (item.Kind != SearchItemKind.App)
        {
            return flyout;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var locationTarget = GetSearchItemLocationTarget(item);
        var location = new MenuFlyoutItem
        {
            Text = "打开文件位置",
            IsEnabled = _quickLaunchActions.CanOpenLocation(QuickLaunchItemKind.App, locationTarget)
        };
        location.Click += (_, _) => _quickLaunchActions.OpenLocation(QuickLaunchItemKind.App, locationTarget);
        flyout.Items.Add(location);

        var isPinned = _quickLaunchItems.Contains(QuickLaunchItemKind.App, item.Payload);
        var pin = new MenuFlyoutItem
        {
            Text = isPinned ? "取消固定到快速启动" : "固定到快速启动"
        };
        pin.Click += (_, _) => ToggleSearchItemQuickLaunchPin(item);
        flyout.Items.Add(pin);

        var admin = new MenuFlyoutItem
        {
            Text = "以管理员方式打开",
            IsEnabled = _quickLaunchActions.CanRunAsAdmin(item.Payload)
        };
        admin.Click += (_, _) => _quickLaunchActions.RunAsAdmin(item.Payload);
        flyout.Items.Add(admin);

        if (QuickLaunchActionService.IsFileSystemPath(GetSearchItemLocationTarget(item)))
        {
            var copy = new MenuFlyoutItem { Text = "复制路径" };
            copy.Click += (_, _) => CopySearchItemPath(item);
            flyout.Items.Add(copy);
        }

        return flyout;
    }

    private void ToggleSearchItemQuickLaunchPin(SearchItem item)
    {
        if (item.Kind != SearchItemKind.App)
        {
            return;
        }

        if (_quickLaunchItems.Contains(QuickLaunchItemKind.App, item.Payload))
        {
            _quickLaunchItems.DeleteByTarget(QuickLaunchItemKind.App, item.Payload);
            return;
        }

        _quickLaunchItems.AddOrUpdate(new QuickLaunchItem(
            Id: 0,
            Kind: QuickLaunchItemKind.App,
            Title: item.Title,
            Target: item.Payload,
            SortOrder: 0,
            CreatedUtc: DateTime.UtcNow));
    }

    private static string GetSearchItemLocationTarget(SearchItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.LocationPath))
        {
            return item.LocationPath;
        }

        return item.Payload;
    }

    private static void CopySearchItemPath(SearchItem item)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(GetSearchItemLocationTarget(item));
        Clipboard.SetContent(package);
    }

    private async void ExecuteSearchItem(SearchItem item)
    {
        if (item.Kind == SearchItemKind.Command)
        {
            var command = _commandRegistry.FindById(item.Payload);
            if (command is not null)
            {
                await ExecuteCommandAsync(command);
            }

            ClearSearch();
            return;
        }

        if (item.Kind is SearchItemKind.File or SearchItemKind.Folder or SearchItemKind.Web)
        {
            if (LaunchDirectSearchAction(item))
            {
                HideDrawer();
            }

            ClearSearch();
            return;
        }

        if (item.Kind == SearchItemKind.Tool)
        {
            SelectToolFromHome(item.Payload);
        }
        else
        {
            InstalledAppLauncher.Launch(item.Payload);
            HideDrawer();
        }

        // 记录到最近使用（存 SQLite，首页「最近使用」区域展示）。
        var kind = item.Kind == SearchItemKind.Tool
            ? RecentItemKind.Tool
            : RecentItemKind.App;
        _recentItems.Touch(new RecentItem(
            kind,
            item.Payload,
            item.Title,
            item.SubTitle,
            item.Glyph,
            DateTime.UtcNow));

        ClearSearch();
    }

    private static bool LaunchDirectSearchAction(SearchItem item)
    {
        try
        {
            if (item.Kind == SearchItemKind.File)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.Payload}\"",
                    UseShellExecute = true
                });
                return true;
            }

            if (item.Kind == SearchItemKind.Folder)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{item.Payload}\"",
                    UseShellExecute = true
                });
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = item.Payload,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ClearSearch()
    {
        _searchCancellationSource?.Cancel();
        _isNormalizingSearchInput = true;
        SearchInput.Text = string.Empty;
        _isNormalizingSearchInput = false;
        ClearSearchResults();
        UpdateSearchSourceVisuals();
    }

    private void ClearSearchResults()
    {
        _searchResults.Clear();
        SearchPopup.IsOpen = false;
    }

    private void UpdateSearchSourceVisuals()
    {
        if (_isUpdatingSearchSourceVisuals)
        {
            return;
        }

        _isUpdatingSearchSourceVisuals = true;
        try
        {
            var useEverything = _settings.UseEverythingSearch;
            var everythingState = _searchCoordinator.EverythingState;
            SearchInput.PlaceholderText = useEverything
                ? everythingState switch
                {
                    EverythingSearchState.MissingBridge => "Everything 未启动",
                    EverythingSearchState.Loading => "Everything 加载中…",
                    EverythingSearchState.Searching => "Everything 搜索文件",
                    _ => "Everything 搜索文件"
                }
                : "搜索应用或工具";

            var loading = useEverything && everythingState is EverythingSearchState.Loading or EverythingSearchState.Searching;
            EverythingSearchRing.IsActive = loading;
            EverythingSearchRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

            EverythingSearchChrome.BorderThickness = useEverything ? new Thickness(1) : new Thickness(0);
            EverythingSearchChrome.BorderBrush = useEverything
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeAccentBrush"]
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            EverythingSearchChrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            EverythingSearchBadge.Foreground = useEverything
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeAccentBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeMutedBrush"];
            EverythingSearchBadge.Opacity = loading || everythingState == EverythingSearchState.MissingBridge
                ? 0.45
                : 1.0;

            EverythingSearchToggle.SetValue(
                ToolTipService.ToolTipProperty,
                useEverything
                    ? everythingState switch
                    {
                        EverythingSearchState.MissingBridge => "Everything 未启动",
                        EverythingSearchState.Loading => "Everything 加载中",
                        EverythingSearchState.Searching => "Everything 搜索中",
                        EverythingSearchState.Ready => "Everything 搜索中",
                        _ => "Everything 搜索"
                    }
                    : "Everything 搜索");
        }
        finally
        {
            _isUpdatingSearchSourceVisuals = false;
        }
    }

    private async void OnEverythingSearchToggled(object sender, RoutedEventArgs e)
        => await ToggleEverythingSearchModeAsync();

    private async Task ToggleEverythingSearchModeAsync()
    {
        if (_isUpdatingSearchSourceVisuals)
        {
            return;
        }

        var enabled = !_settings.UseEverythingSearch;
        if (_settings.UseEverythingSearch == enabled)
        {
            return;
        }

        _settings.UseEverythingSearch = enabled;
        _settings.Save();
        _searchCoordinator.SetEverythingEnabled(enabled);
        UpdateSearchSourceVisuals();

        if (!string.IsNullOrWhiteSpace(SearchInput.Text))
        {
            await RefreshSearchResultsAsync();
        }
        else
        {
            ClearSearchResults();
        }
    }
}
