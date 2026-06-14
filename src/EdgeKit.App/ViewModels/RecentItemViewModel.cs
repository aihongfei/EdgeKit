using EdgeKit.Core.Recent;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 首页列表项的展示模型。包裹一条 <see cref="RecentItem"/>，附加 UI 展示所需的解析后图标
/// 与首字母占位文本。窗口项可能有应用图标（<see cref="IconImage"/>），工具项用字形（<see cref="Glyph"/>）。
/// </summary>
public sealed class RecentItemViewModel
{
    public RecentItemViewModel(RecentItem model, BitmapImage? iconImage)
    {
        Model = model;
        IconImage = iconImage;
    }

    /// <summary>底层数据。</summary>
    public RecentItem Model { get; }

    /// <summary>窗口应用图标；为 null 时用 <see cref="Glyph"/> 或 <see cref="FallbackLetter"/> 占位。</summary>
    public BitmapImage? IconImage { get; }

    public string Title => Model.Title;

    public string SubTitle => Model.SubTitle;

    public string Glyph => Model.Glyph;

    /// <summary>无图标时的首字母占位（标题或副标题首字符，大写）。</summary>
    public string FallbackLetter
    {
        get
        {
            var source = !string.IsNullOrWhiteSpace(Model.Title) ? Model.Title : Model.SubTitle;
            return string.IsNullOrEmpty(source)
                ? "?"
                : source[..1].ToUpperInvariant();
        }
    }

    /// <summary>是否有可显示的位图图标。</summary>
    public bool HasIconImage => IconImage is not null;

    /// <summary>无位图图标（用于占位元素的可见性绑定）。</summary>
    public bool HasNoIconImage => IconImage is null;
}