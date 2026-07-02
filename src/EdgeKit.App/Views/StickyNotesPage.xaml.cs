using EdgeKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EdgeKit.App.Views;

/// <summary>
/// 桌面便签管理页：列出所有便签，支持新建、打开/关闭与删除。
/// </summary>
public sealed partial class StickyNotesPage : Page
{
    private StickyNotesViewModel? _viewModel;

    public StickyNotesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StickyNotesPageParameter parameter)
        {
            _viewModel = parameter.ViewModel;
            _viewModel.NotesChanged += OnNotesChanged;
            Refresh();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_viewModel is not null)
        {
            _viewModel.NotesChanged -= OnNotesChanged;
        }
    }

    private void OnNotesChanged(object? sender, System.EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        _viewModel?.Refresh();
        NotesList.ItemsSource = _viewModel?.Items;
        EmptyHint.Visibility = _viewModel?.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnCreateNewClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.CreateNew();
    }

    private void OnNoteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StickyNoteItemViewModel item)
        {
            item.ToggleOpen();
        }
    }

    private void OnToggleOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StickyNoteItemViewModel item })
        {
            item.ToggleOpen();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StickyNoteItemViewModel item })
        {
            item.Delete();
        }
    }
}