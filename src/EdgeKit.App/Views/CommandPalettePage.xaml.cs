using System;
using System.Collections.ObjectModel;
using System.Linq;
using EdgeKit.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace EdgeKit.App.Views;

/// <summary>系统命令中心页面。</summary>
public sealed partial class CommandPalettePage : Page
{
    private const double CardMinWidth = 180;
    private const double CardSpacing = 12;

    private readonly ObservableCollection<CommandDescriptor> _commands = new();

    private CommandPalettePageParameter? _parameter;
    private string? _selectedCategory;

    public CommandPalettePage()
    {
        InitializeComponent();
        CommandCardsGrid.ItemsSource = _commands;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not CommandPalettePageParameter parameter)
        {
            return;
        }

        _parameter = parameter;
        parameter.Registry.Changed += OnCommandsChanged;
        BuildCategoryFilterBar();
        RefreshCommands();
        CommandSearchInput.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_parameter is not null)
        {
            _parameter.Registry.Changed -= OnCommandsChanged;
        }

        base.OnNavigatedFrom(e);
    }

    private void OnCommandsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BuildCategoryFilterBar();
            RefreshCommands();
        });
    }

    private void BuildCategoryFilterBar()
    {
        if (_parameter is null)
        {
            return;
        }

        var categories = _parameter.Registry.GetAll()
            .Select(c => c.Category)
            .Append("自定义命令")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetCategoryOrder)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_selectedCategory is not null
            && !categories.Contains(_selectedCategory, StringComparer.OrdinalIgnoreCase))
        {
            _selectedCategory = null;
        }

        CategoryFilterBar.Items.Clear();
        CategoryFilterBar.Items.Add(CreateFilterChip("全部", category: null, _selectedCategory is null));

        foreach (var category in categories)
        {
            CategoryFilterBar.Items.Add(CreateFilterChip(
                category,
                category,
                string.Equals(_selectedCategory, category, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private Button CreateFilterChip(string label, string? category, bool isSelected)
    {
        var chip = new Button
        {
            Content = label,
            Tag = category,
            MinHeight = 32,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(7),
            Background = isSelected
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeAccentSoftBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeControlBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EdgeLineBrush"]
        };

        chip.Click += (_, _) => SelectCategory(category);
        return chip;
    }

    private void SelectCategory(string? category)
    {
        _selectedCategory = category;
        BuildCategoryFilterBar();
        RefreshCommands();
    }

    private void OnCommandSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        if (_parameter is null)
        {
            return;
        }

        var query = CommandSearchInput.Text ?? string.Empty;
        var source = string.IsNullOrWhiteSpace(query)
            ? _parameter.Registry.GetAll()
            : _parameter.Registry.Search(query);

        if (_selectedCategory is not null)
        {
            source = source
                .Where(c => string.Equals(c.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _commands.Clear();
        foreach (var command in source)
        {
            _commands.Add(command);
        }

        EmptyHint.Visibility = _commands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandCardsGrid.Visibility = _commands.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCommandItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandDescriptor command)
        {
            await ExecuteCommandAsync(command);
        }
    }

    private async void OnCommandSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                FocusCommandCard(0);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (_commands.FirstOrDefault() is { } command)
                {
                    await ExecuteCommandAsync(command);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Escape:
                if (!string.IsNullOrWhiteSpace(CommandSearchInput.Text))
                {
                    CommandSearchInput.Text = string.Empty;
                }
                else
                {
                    _parameter?.HideDrawer();
                }

                e.Handled = true;
                break;
        }
    }

    private async void OnCommandGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up when IsFirstCommandRowSelected():
                CommandSearchInput.Focus(FocusState.Programmatic);
                CommandSearchInput.SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (CommandCardsGrid.SelectedItem is CommandDescriptor command)
                {
                    await ExecuteCommandAsync(command);
                    e.Handled = true;
                }
                break;

            case VirtualKey.Escape:
                if (!string.IsNullOrWhiteSpace(CommandSearchInput.Text))
                {
                    CommandSearchInput.Text = string.Empty;
                    CommandSearchInput.Focus(FocusState.Programmatic);
                }
                else
                {
                    _parameter?.HideDrawer();
                }

                e.Handled = true;
                break;
        }
    }

    private bool IsFirstCommandRowSelected()
    {
        if (CommandCardsGrid.SelectedIndex <= 0)
        {
            return true;
        }

        var columns = GetCurrentColumnCount();
        return CommandCardsGrid.SelectedIndex < columns;
    }

    private void FocusCommandCard(int index)
    {
        if (_commands.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _commands.Count - 1);
        CommandCardsGrid.SelectedIndex = index;
        CommandCardsGrid.ScrollIntoView(CommandCardsGrid.SelectedItem);
        CommandCardsGrid.UpdateLayout();

        if (CommandCardsGrid.ContainerFromIndex(index) is GridViewItem item)
        {
            item.Focus(FocusState.Programmatic);
        }
        else
        {
            CommandCardsGrid.Focus(FocusState.Programmatic);
        }
    }

    private void OnCommandGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CommandCardsGrid.ItemsPanelRoot is not ItemsWrapGrid wrap)
        {
            return;
        }

        var available = e.NewSize.Width;
        if (available <= 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)(available / (CardMinWidth + CardSpacing)));
        wrap.ItemWidth = Math.Floor(available / columns);
    }

    private int GetCurrentColumnCount()
    {
        if (CommandCardsGrid.ItemsPanelRoot is ItemsWrapGrid { ItemWidth: > 0 } wrap
            && CommandCardsGrid.ActualWidth > 0)
        {
            return Math.Max(1, (int)Math.Floor(CommandCardsGrid.ActualWidth / wrap.ItemWidth));
        }

        return 1;
    }

    private async System.Threading.Tasks.Task ExecuteCommandAsync(CommandDescriptor command)
    {
        if (_parameter is null)
        {
            return;
        }

        await _parameter.ExecuteCommand(command);
        RefreshCommands();
    }

    private async void OnAddCustomCommandClick(object sender, RoutedEventArgs e)
    {
        if (_parameter is null)
        {
            return;
        }

        var command = await PromptCustomCommandAsync();
        if (command is null)
        {
            return;
        }

        _parameter.CustomCommands.Add(command);
        _selectedCategory = "自定义命令";
        BuildCategoryFilterBar();
        RefreshCommands();
    }

    private async System.Threading.Tasks.Task<CustomCommand?> PromptCustomCommandAsync()
    {
        var titleBox = new TextBox { PlaceholderText = "名称" };
        var commandBox = new TextBox { PlaceholderText = "命令内容 / 路径 / URI" };
        var argumentsBox = new TextBox { PlaceholderText = "参数（可选）" };
        var workingDirectoryBox = new TextBox { PlaceholderText = "工作目录（可选）" };
        var keywordsBox = new TextBox { PlaceholderText = "关键词（可选，用空格或逗号分隔）" };
        var adminBox = new CheckBox { Content = "以管理员方式运行" };
        var confirmBox = new CheckBox { Content = "执行前确认" };

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(titleBox);
        panel.Children.Add(commandBox);
        panel.Children.Add(argumentsBox);
        panel.Children.Add(workingDirectoryBox);
        panel.Children.Add(keywordsBox);
        panel.Children.Add(adminBox);
        panel.Children.Add(confirmBox);

        var dialog = new ContentDialog
        {
            Title = "添加自定义命令",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text) || string.IsNullOrWhiteSpace(commandBox.Text))
            {
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        return new CustomCommand(
            Id: 0,
            Title: titleBox.Text.Trim(),
            CommandText: commandBox.Text.Trim(),
            Arguments: argumentsBox.Text.Trim(),
            WorkingDirectory: workingDirectoryBox.Text.Trim(),
            Glyph: "\uE756",
            Keywords: keywordsBox.Text.Trim(),
            RunAsAdministrator: adminBox.IsChecked == true,
            RequiresConfirmation: confirmBox.IsChecked == true,
            CreatedUtc: now,
            UpdatedUtc: now);
    }

    private void OnCommandItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommandDescriptor command } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var execute = new MenuFlyoutItem { Text = "执行" };
        execute.Click += async (_, _) => await ExecuteCommandAsync(command);
        flyout.Items.Add(execute);

        if (command.IsCustom && command.CustomCommandId is long customId)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutItem { Text = "删除自定义命令" };
            delete.Click += async (_, _) => await DeleteCustomCommandAsync(command.Title, customId);
            flyout.Items.Add(delete);
        }

        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = e.GetPosition(anchor) });
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task DeleteCustomCommandAsync(string title, long id)
    {
        if (_parameter is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除自定义命令",
            Content = $"确定删除「{title}」？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _parameter.CustomCommands.Delete(id);
            BuildCategoryFilterBar();
            RefreshCommands();
        }
    }

    private static int GetCategoryOrder(string category)
        => category switch
        {
            "Windows 设置" => 0,
            "系统工具" => 1,
            "Shell 入口" => 2,
            "系统动作" => 3,
            "自定义命令" => 4,
            _ => 100
        };
}
