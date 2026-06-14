using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.Search;

/// <summary>搜索结果的来源类型。</summary>
public enum SearchItemKind
{
    /// <summary>软件内置工具。</summary>
    Tool,

    /// <summary>本机已安装应用。</summary>
    App,

    /// <summary>历史搜索记录。</summary>
    History,

    /// <summary>命令面板命令。</summary>
    Command,

    /// <summary>本地文件。</summary>
    File,

    /// <summary>本地文件夹。</summary>
    Folder,

    /// <summary>网页地址。</summary>
    Web
}

/// <summary>
/// 统一搜索结果项，供搜索框下拉列表绑定。
/// 工具项用字形（<see cref="Glyph"/>）渲染图标，应用项用位图（<see cref="IconImage"/>）。
/// <see cref="Payload"/> 为工具 Id 或应用 AppUserModelId，执行时据此路由。
/// </summary>
public sealed class SearchItem
{
    public SearchItem(
        SearchItemKind kind,
        string title,
        string subTitle,
        string glyph,
        BitmapImage? iconImage,
        string payload,
        string locationPath = "")
    {
        Kind = kind;
        Title = title;
        SubTitle = subTitle;
        Glyph = glyph;
        IconImage = iconImage;
        Payload = payload;
        LocationPath = locationPath;
    }

    public SearchItemKind Kind { get; }

    public string Title { get; }

    public string SubTitle { get; }

    public string Glyph { get; }

    public BitmapImage? IconImage { get; }

    public string Payload { get; }

    public string LocationPath { get; }

    /// <summary>右侧类型标签文本。</summary>
    public string KindLabel => Kind switch
    {
        SearchItemKind.Tool => "工具",
        SearchItemKind.App => "应用",
        SearchItemKind.History => "历史",
        SearchItemKind.Command => "命令",
        SearchItemKind.File => "文件",
        SearchItemKind.Folder => "文件夹",
        SearchItemKind.Web => "网页",
        _ => ""
    };

    /// <summary>是否有位图图标（用于模板可见性绑定）。</summary>
    public bool HasIconImage => IconImage is not null;

    /// <summary>是否用字形图标（工具项，或应用无位图时回退）。</summary>
    public Visibility GlyphVisibility =>
        IconImage is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>是否用位图图标。</summary>
    public Visibility IconVisibility =>
        IconImage is null ? Visibility.Collapsed : Visibility.Visible;
}
