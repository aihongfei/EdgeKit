using System;
using System.Globalization;
using System.Linq;
using EdgeKit.App.ViewModels;
using EdgeKit.Core.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EdgeKit.App.Views;

public sealed partial class TaskBoardPage : Page
{
    private TaskBoardViewModel? _viewModel;
    private TaskCardViewModel? _selectedCard;

    public TaskBoardPage()
    {
        InitializeComponent();
        SaveButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not TaskBoardPageParameter parameter)
        {
            return;
        }

        _viewModel = parameter.ViewModel;
        TaskList.ItemsSource = _viewModel.GroupedItems;
        FilterTabs.ItemsSource = _viewModel.Filters;
        _viewModel.Refresh();
        _viewModel.RepositoryChanged += OnRepositoryChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_viewModel is not null)
        {
            _viewModel.RepositoryChanged -= OnRepositoryChanged;
        }
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewModel?.Refresh();
            // Refresh 会重建 GroupedItems 和其中所有 ViewModel 实例，
            // 必须清空选中态，否则 _selectedCard 会指向已失效的实例。
            TaskList.SelectedItem = null;
        });
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dueLocal = ResolveDueLocal();
        var priority = ResolvePriority();
        _viewModel.Add(TitleInput.Text, DescriptionInput.Text, dueLocal, priority);

        TitleInput.Text = string.Empty;
        DescriptionInput.Text = string.Empty;
        SetDueInput(null);
        PriorityInput.SelectedIndex = 1;
        TaskList.SelectedItem = null;
        TitleInput.Focus(FocusState.Programmatic);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _selectedCard is null)
        {
            return;
        }

        var dueLocal = ResolveDueLocal();
        var priority = ResolvePriority();
        _viewModel.Update(_selectedCard, TitleInput.Text, DescriptionInput.Text, dueLocal, priority);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _selectedCard is null)
        {
            return;
        }

        var card = _selectedCard;
        // 先清空选中态，让 SelectionChanged 立即禁用按钮、清空表单，
        // 避免 Refresh() 重建实例后 _selectedCard 变成失效引用。
        TaskList.SelectedItem = null;
        _viewModel.Delete(card);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || FilterTabs.SelectedItem is not TaskBoardFilterItem filter)
        {
            return;
        }

        _viewModel.Filter = filter.Status;
    }

    private void OnCardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskCardViewModel card)
        {
            _selectedCard = card;
            TitleInput.Text = card.Title;
            DescriptionInput.Text = card.Description;
            SetDueInput(card.DueLocal);
            PriorityInput.SelectedIndex = card.Priority switch
            {
                TaskCardPriority.Low => 0,
                TaskCardPriority.High => 2,
                _ => 1
            };
            SaveButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
            return;
        }

        _selectedCard = null;
        TitleInput.Text = string.Empty;
        DescriptionInput.Text = string.Empty;
        SetDueInput(null);
        PriorityInput.SelectedIndex = 1;
        SaveButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
    }

    private void OnCardStatusFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || flyout.Target is not Button button || button.Tag is not TaskCardViewModel card)
        {
            return;
        }

        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
        {
            item.Tag = card;
            item.IsEnabled = !int.TryParse((string)item.DataContext, out var index) || index != (int)card.Status;
        }
    }

    private void OnCardStatusFlyoutItemClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not MenuFlyoutItem item || item.Tag is not TaskCardViewModel card)
        {
            return;
        }

        if (!int.TryParse((string)item.DataContext, out var index))
        {
            return;
        }

        var status = (TaskCardStatus)index;
        if (card.Status != status)
        {
            _viewModel.Move(card, status);
        }
    }

    private TaskCardPriority ResolvePriority()
        => PriorityInput.SelectedIndex switch
        {
            0 => TaskCardPriority.Low,
            2 => TaskCardPriority.High,
            _ => TaskCardPriority.Normal
        };

    private DateTime? ResolveDueLocal()
    {
        if (DueDateInput.SelectedDate is null)
        {
            return null;
        }

        var date = DueDateInput.SelectedDate.Value.Date;
        var time = DueTimeInput.SelectedTime?.TimeOfDay ?? TimeSpan.Zero;
        return date.Add(time);
    }

    private void SetDueInput(DateTime? dueLocal)
    {
        if (dueLocal is null)
        {
            DueDateInput.SelectedDate = null;
            DueTimeInput.SelectedTime = null;
            return;
        }

        var due = new DateTimeOffset(dueLocal.Value);
        DueDateInput.SelectedDate = due;
        DueTimeInput.SelectedTime = due;
    }
}