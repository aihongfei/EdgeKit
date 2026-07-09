using System;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Clipboard;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace EdgeKit.App.Views;

/// <summary>
/// 剪贴板历史页。WrapGrid 自适应换行展示历史卡片，单击复制、右键弹菜单（复制/删除/切换分组/置顶），
/// 顶部分组筛选条切换分组，工具条提供新建分组与清空。订阅仓储变更实时刷新。
/// </summary>
public sealed partial class ClipboardHistoryPage : Page
{
    private ClipboardHistoryViewModel? _viewModel;

    // 右键当前卡片对应的 VM，供 Flyout 构建与命令使用。
    private ClipboardItemViewModel? _contextItem;
    private DispatcherQueueTimer? _copyToastTimer;

    public ClipboardHistoryPage()
    {
        InitializeComponent();

        _copyToastTimer = DispatcherQueue.CreateTimer();
        _copyToastTimer.Interval = TimeSpan.FromSeconds(1.4);
        _copyToastTimer.IsRepeating = false;
        _copyToastTimer.Tick += OnCopyToastTimerTick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ClipboardHistoryViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.Refresh();

        ItemsGrid.ItemsSource = _viewModel.Items;
        ClipboardSearchInput.Text = _viewModel.SearchKeyword;
        BuildGroupFilterBar();
        UpdateEmptyState();
        UpdateAutoGroupButtonState();

        _viewModel.RepositoryChanged += OnRepositoryChanged;
        _viewModel.AutoGroupingChanged += OnAutoGroupingChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_viewModel is not null)
        {
            _viewModel.RepositoryChanged -= OnRepositoryChanged;
            _viewModel.AutoGroupingChanged -= OnAutoGroupingChanged;
        }
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewModel?.Refresh();
            BuildGroupFilterBar();
            UpdateEmptyState();
        });
    }

    private void OnAutoGroupingChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateAutoGroupButtonState();
            _viewModel?.Refresh();
            BuildGroupFilterBar();
            UpdateEmptyState();
        });
    }

    private void UpdateEmptyState()
    {
        if (_viewModel is null)
        {
            return;
        }

        var empty = _viewModel.HasNoItems;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>构建顶部分组筛选条：全部 + 各分组芯片。当前选中项高亮。</summary>
    private void BuildGroupFilterBar()
    {
        if (_viewModel is null)
        {
            return;
        }

        GroupFilterBar.Items.Clear();
        GroupFilterBarExpanded.Items.Clear();

        // “全部”芯片。
        GroupFilterBar.Items.Add(CreateFilterChip("全部", groupId: null,
            isSelected: _viewModel.SelectedGroupId is null));
        GroupFilterBarExpanded.Items.Add(CreateFilterChip("全部", groupId: null,
            isSelected: _viewModel.SelectedGroupId is null));

        foreach (var group in _viewModel.Groups)
        {
            GroupFilterBar.Items.Add(CreateFilterChip(group.Name, group.Id,
                isSelected: _viewModel.SelectedGroupId == group.Id, group.Id));
            GroupFilterBarExpanded.Items.Add(CreateFilterChip(group.Name, group.Id,
                isSelected: _viewModel.SelectedGroupId == group.Id, group.Id));
        }
    }

    private Button CreateFilterChip(string label, long? groupId, bool isSelected, long? manageGroupId = null)
    {
        var chip = new Button
        {
            Content = label,
            Tag = groupId,
            Margin = new Thickness(0),
            MinHeight = 32,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(7),
            Background = isSelected
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeAccentSoftBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeControlBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeLineBrush"]
        };

        chip.Click += (_, _) => SelectGroupFilter(groupId);

        // 真实分组芯片：右键可重命名/删除。
        if (manageGroupId is not null)
        {
            var flyout = new MenuFlyout();

            var rename = new MenuFlyoutItem { Text = "重命名分组" };
            rename.Click += async (_, _) => await RenameGroupAsync(manageGroupId.Value, label);
            flyout.Items.Add(rename);

            var delete = new MenuFlyoutItem { Text = "删除分组" };
            delete.Click += (_, _) => _viewModel?.DeleteGroup(manageGroupId.Value);
            flyout.Items.Add(delete);

            chip.ContextFlyout = flyout;
        }

        return chip;
    }

    private void SelectGroupFilter(long? groupId)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SetGroupFilter(groupId);
        BuildGroupFilterBar();
        UpdateEmptyState();
    }

    private void OnClipboardSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SetSearchKeyword(ClipboardSearchInput.Text);
        UpdateEmptyState();
    }

    /// <summary>单击卡片：直接复制内容到剪贴板。</summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipboardItemViewModel vm)
        {
            CopyItem(vm);
        }
    }

    private void CopyItem(ClipboardItemViewModel item)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            _viewModel.Copy(item);
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

        _copyToastTimer?.Stop();
        _copyToastTimer?.Start();
    }

    private void OnCopyToastTimerTick(DispatcherQueueTimer sender, object args)
    {
        CopyToast.Visibility = Visibility.Collapsed;
    }

    // 卡片最小宽度与卡片间距（与 ItemContainerStyle / ItemsGrid 右侧负边距一致）。
    private const double CardMinWidth = 180;
    private const double CardSpacing = 12;

    /// <summary>
    /// 根据可用宽度计算列数并均分列宽，使卡片网格右边缘与内容区对齐（不留固定空隙）。
    /// </summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ItemsGrid.ItemsPanelRoot is not ItemsWrapGrid wrap)
        {
            return;
        }

        var available = e.NewSize.Width;
        if (available <= 0)
        {
            return;
        }

        // 列数：每个 layout slot 含右侧 CardSpacing，ItemsGrid 的负右边距会把最后一列
        // 的间距抵消掉，使卡片视觉右边缘与内容区右边缘对齐。
        var columns = Math.Max(1, (int)(available / (CardMinWidth + CardSpacing)));

        // 均分 slot 宽度；GridViewItem 的右 Margin 形成列间距，卡片本体随剩余宽度拉伸。
        var itemWidth = available / columns;
        wrap.ItemWidth = Math.Floor(itemWidth);
    }

    /// <summary>右键卡片：记录上下文项，供 Flyout 构建与命令使用。</summary>
    private void OnCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardItemViewModel vm })
        {
            _contextItem = vm;
        }
    }

    /// <summary>右键菜单打开时动态构建项：复制、删除、置顶、切换分组（列出所有分组 + 移出分组）。</summary>
    private void OnCardFlyoutOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout || _viewModel is null || _contextItem is null)
        {
            return;
        }

        var item = _contextItem;
        flyout.Items.Clear();

        var copy = new MenuFlyoutItem
        {
            Text = "复制",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copy.Click += (_, _) => CopyItem(item);
        flyout.Items.Add(copy);

        var pin = new MenuFlyoutItem
        {
            Text = item.Pinned ? "取消固定" : "固定",
            Icon = new FontIcon { Glyph = "\uE718" }
        };
        pin.Click += (_, _) => _viewModel.TogglePin(item);
        flyout.Items.Add(pin);

        var delete = new MenuFlyoutItem
        {
            Text = "删除",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        delete.Click += (_, _) => _viewModel.Delete(item);
        flyout.Items.Add(delete);

        // 图片条目支持 OCR 识别文字。
        if (item.Model.Kind == ClipboardItemKind.Image)
        {
            var recognize = new MenuFlyoutItem
            {
                Text = "识别文字",
                Icon = new FontIcon { Glyph = "\uE8D4" }
            };
            recognize.Click += async (_, _) => await RecognizeImageTextAsync(item);
            flyout.Items.Add(recognize);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // 切换分组子菜单：列出所有分组 + 移出分组。
        var moveSub = new MenuFlyoutSubItem
        {
            Text = "切换分组",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        var groups = _viewModel.GetGroupsSnapshot();
        foreach (var group in groups)
        {
            var groupItem = new MenuFlyoutItem { Text = group.Name };
            var capturedId = group.Id;
            groupItem.Click += (_, _) => _viewModel.MoveToGroup(item, capturedId);
            moveSub.Items.Add(groupItem);
        }

        if (groups.Count > 0)
        {
            moveSub.Items.Add(new MenuFlyoutSeparator());
        }

        var removeFromGroup = new MenuFlyoutItem { Text = "移出分组" };
        removeFromGroup.Click += (_, _) => _viewModel.MoveToGroup(item, null);
        moveSub.Items.Add(removeFromGroup);

        var newGroupAndMove = new MenuFlyoutItem { Text = "新建分组并移入…" };
        newGroupAndMove.Click += async (_, _) => await NewGroupAndMoveAsync(item);
        moveSub.Items.Add(newGroupAndMove);

        flyout.Items.Add(moveSub);
    }

    private async void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptGroupNameAsync("新建分组", string.Empty);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel?.AddGroup(name.Trim(), "#78B7FF");
        }
    }

    private async void OnAutoGroupClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsAutoGrouping)
        {
            return;
        }

        var (startUtc, endUtc) = await ShowAutoGroupDialogAsync();
        if (startUtc is null || endUtc is null)
        {
            return;
        }

        ShowCopyToast("正在智能分组…", success: true);
        try
        {
            await _viewModel.AutoGroupAsync(startUtc.Value, endUtc.Value);
            ShowCopyToast("自动分组完成", success: true);
        }
        catch (Exception ex)
        {
            ShowCopyToast("自动分组失败: " + ex.Message, success: false);
        }
    }

    private void UpdateAutoGroupButtonState()
    {
        if (_viewModel is null)
        {
            return;
        }

        var loading = _viewModel.IsAutoGrouping;
        AutoGroupButton.IsEnabled = !loading;
        AutoGroupNormalContent.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        AutoGroupLoadingContent.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _isGroupFilterExpanded;

    private void OnToggleGroupExpandClick(object sender, RoutedEventArgs e)
    {
        _isGroupFilterExpanded = !_isGroupFilterExpanded;
        UpdateGroupFilterLayout();
    }

    private void UpdateGroupFilterLayout()
    {
        if (_isGroupFilterExpanded)
        {
            GroupFilterCollapsedRow.Visibility = Visibility.Collapsed;
            GroupFilterExpandedRow.Visibility = Visibility.Visible;

            MoveSearchBox(CollapsedSearchPlaceholder, ExpandedSearchPlaceholder);
            ExpandGroupsIcon.Glyph = "\uE972";
            SyncExpandedGroupWidth();
        }
        else
        {
            GroupFilterCollapsedRow.Visibility = Visibility.Visible;
            GroupFilterExpandedRow.Visibility = Visibility.Collapsed;

            MoveSearchBox(ExpandedSearchPlaceholder, CollapsedSearchPlaceholder);
            ExpandGroupsIcon.Glyph = "\uE970";
        }
    }

    private void OnExpandedGroupScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncExpandedGroupWidth();
    }

    private void SyncExpandedGroupWidth()
    {
        var width = ExpandedGroupScrollViewer.ActualWidth;
        if (width > 0)
        {
            GroupFilterBarExpanded.Width = width;
        }
    }

    private static void MoveSearchBox(Panel from, Panel to)
    {
        if (from.Children.Count == 0)
        {
            return;
        }

        var searchBox = from.Children[0];
        from.Children.RemoveAt(0);
        to.Children.Add(searchBox);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearCurrent();
    }

    private async System.Threading.Tasks.Task RenameGroupAsync(long groupId, string currentName)
    {
        var name = await PromptGroupNameAsync("重命名分组", currentName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel?.RenameGroup(groupId, name.Trim());
        }
    }

    private async System.Threading.Tasks.Task NewGroupAndMoveAsync(ClipboardItemViewModel item)
    {
        var name = await PromptGroupNameAsync("新建分组并移入", string.Empty);
        if (string.IsNullOrWhiteSpace(name) || _viewModel is null)
        {
            return;
        }

        var id = _viewModel.AddGroup(name.Trim(), "#78B7FF");
        _viewModel.MoveToGroup(item, id);
    }

    /// <summary>弹出输入框获取分组名。返回 null 表示取消。</summary>
    private async System.Threading.Tasks.Task<string?> PromptGroupNameAsync(string title, string initial)
    {
        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = "分组名称",
            SelectionStart = initial.Length
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Text : null;
    }

    /// <summary>弹出日期范围选择对话框，返回 (startUtc, endUtc)；取消返回 (null, null)。</summary>
    private async System.Threading.Tasks.Task<(DateTime? StartUtc, DateTime? EndUtc)> ShowAutoGroupDialogAsync()
    {
        var presets = new[]
        {
            "当天",
            "昨天",
            "近一周",
            "近一个月",
            "自定义"
        };

        var presetCombo = new ComboBox
        {
            Header = "时间范围",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        foreach (var preset in presets)
        {
            presetCombo.Items.Add(preset);
        }

        var startPicker = new CalendarDatePicker
        {
            Header = "开始日期",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Date = DateTime.Today
        };
        var endPicker = new CalendarDatePicker
        {
            Header = "结束日期",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Date = DateTime.Today
        };

        void ApplyPreset(int index)
        {
            var today = DateTime.Today;
            startPicker.IsEnabled = index == 4;
            endPicker.IsEnabled = index == 4;

            (startPicker.Date, endPicker.Date) = index switch
            {
                0 => (today, today),
                1 => (today.AddDays(-1), today.AddDays(-1)),
                2 => (today.AddDays(-6), today),
                3 => (today.AddDays(-29), today),
                _ => (startPicker.Date, endPicker.Date)
            };
        }

        ApplyPreset(0);
        presetCombo.SelectionChanged += (_, _) => ApplyPreset(presetCombo.SelectedIndex);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(presetCombo);
        content.Children.Add(startPicker);
        content.Children.Add(endPicker);

        var dialog = new ContentDialog
        {
            Title = "自动分组",
            Content = content,
            PrimaryButtonText = "开始分组",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return (null, null);
        }

        if (startPicker.Date is not { } startLocal || endPicker.Date is not { } endLocal)
        {
            return (null, null);
        }

        var startUtc = startLocal.DateTime.ToUniversalTime();
        var endUtc = endLocal.DateTime.AddDays(1).AddTicks(-1).ToUniversalTime();
        return (startUtc, endUtc);
    }

    /// <summary>对图片剪贴板条目执行 OCR，并在识别完成后显示结果对话框。</summary>
    private async System.Threading.Tasks.Task RecognizeImageTextAsync(ClipboardItemViewModel item)
    {
        if (_viewModel is null)
        {
            return;
        }

        ShowCopyToast("正在识别文字…", success: true);
        try
        {
            var result = await _viewModel.RecognizeTextAsync(item);
            if (!result.IsSuccess)
            {
                ShowCopyToast(result.Message, success: false);
                return;
            }

            await ShowOcrResultDialogAsync(result.Text);
        }
        catch (Exception ex)
        {
            ShowCopyToast("识别失败: " + ex.Message, success: false);
        }
    }

    /// <summary>显示 OCR 结果对话框，提供复制到剪贴板和保存到历史两个操作。</summary>
    private async System.Threading.Tasks.Task ShowOcrResultDialogAsync(string text)
    {
        var textBox = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Height = 240
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        var dialog = new ContentDialog
        {
            Title = "识别结果",
            Content = textBox,
            PrimaryButtonText = "复制",
            SecondaryButtonText = "保存到历史",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            ShowCopyToast("已复制到剪贴板", success: true);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _viewModel?.SaveOcrResultAsHistory(text);
            ShowCopyToast("已保存到剪贴板历史", success: true);
        }
    }
}
