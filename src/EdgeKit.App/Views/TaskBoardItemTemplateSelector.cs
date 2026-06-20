using EdgeKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgeKit.App.Views;

public sealed class TaskBoardItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupHeaderTemplate { get; set; }

    public DataTemplate? CardTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is TaskCardGroupHeader ? GroupHeaderTemplate : CardTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}