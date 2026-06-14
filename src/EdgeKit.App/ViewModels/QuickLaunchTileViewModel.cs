using EdgeKit.Core.QuickLaunch;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 首页快速启动磁贴展示模型。最后一个加号磁贴也用此模型表达，便于在同一个网格里展示。
/// </summary>
public sealed class QuickLaunchTileViewModel
{
    public QuickLaunchTileViewModel(QuickLaunchItem model, BitmapImage? iconImage)
    {
        Model = model;
        IconImage = iconImage;
        IsAddTile = false;
    }

    private QuickLaunchTileViewModel()
    {
        IsAddTile = true;
    }

    public QuickLaunchItem? Model { get; }

    public BitmapImage? IconImage { get; }

    public bool IsAddTile { get; }

    public string Title => IsAddTile ? "添加" : Model?.Title ?? string.Empty;

    public string Target => Model?.Target ?? string.Empty;

    public Visibility ItemVisibility => IsAddTile ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AddVisibility => IsAddTile ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IconVisibility =>
        !IsAddTile && IconImage is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GlyphVisibility =>
        !IsAddTile && IconImage is null ? Visibility.Visible : Visibility.Collapsed;

    public string Glyph
    {
        get
        {
            if (IsAddTile)
            {
                return "\uE710";
            }

            return Model?.Kind switch
            {
                QuickLaunchItemKind.Folder => "\uE8B7",
                QuickLaunchItemKind.File => "\uE8A5",
                QuickLaunchItemKind.App => "\uECAA",
                _ => "\uECAA"
            };
        }
    }

    public static QuickLaunchTileViewModel AddTile() => new();
}
