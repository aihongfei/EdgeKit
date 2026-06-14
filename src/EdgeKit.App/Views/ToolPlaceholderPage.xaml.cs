using EdgeKit.Core.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EdgeKit.App.Views;

/// <summary>
/// 统一的工具占位页。接收 <see cref="ToolDescriptor"/>，显示图标、标题、说明与"开发中"标记。
/// 后续每个工具落地时替换为各自的页面。
/// </summary>
public sealed partial class ToolPlaceholderPage : Page
{
    public ToolPlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ToolDescriptor tool)
        {
            ToolIcon.Glyph = tool.Glyph;
            TitleText.Text = tool.Title;
            DescText.Text = tool.Description;
        }
    }
}