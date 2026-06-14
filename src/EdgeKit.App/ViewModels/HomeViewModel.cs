using System;
using System.Collections.ObjectModel;
using EdgeKit.App.Interaction;
using EdgeKit.App.Search;
using EdgeKit.Core.Clipboard;
using EdgeKit.Core.QuickLaunch;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Services;
using EdgeKit.Core.Tools;
using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 首页视图模型：拉取"活动窗口"、"最近使用工具"与"剪贴板历史"三组数据，
/// 转换为展示模型供页面绑定，并封装窗口激活与剪贴板复制逻辑。
/// 每次进入首页时调用 <see cref="Refresh"/> 重新拉取（须在 UI 线程，因创建 BitmapImage）。
/// </summary>
public sealed class HomeViewModel
{
    private const int ClipboardDisplayLimit = 5;

    private readonly IRecentItemsRepository _recentItemsRepository;
    private readonly ISettingsService _settings;
    private readonly IClipboardRepository _clipboardRepository;
    private readonly ClipboardContentWriter _clipboardWriter;
    private readonly IQuickLaunchRepository _quickLaunchRepository;
    private readonly InstalledAppIndex _appIndex;
    private readonly QuickLaunchActionService _quickLaunchActions;
    private readonly IToolCatalog _toolCatalog;
    private readonly WindowManagementService _windowManagement;

    public HomeViewModel(
        IRecentItemsRepository recentItemsRepository,
        ISettingsService settings,
        IClipboardRepository clipboardRepository,
        ClipboardContentWriter clipboardWriter,
        IQuickLaunchRepository quickLaunchRepository,
        InstalledAppIndex appIndex,
        QuickLaunchActionService quickLaunchActions,
        IToolCatalog toolCatalog,
        WindowManagementService windowManagement)
    {
        _recentItemsRepository = recentItemsRepository;
        _settings = settings;
        _clipboardRepository = clipboardRepository;
        _clipboardWriter = clipboardWriter;
        _quickLaunchRepository = quickLaunchRepository;
        _appIndex = appIndex;
        _quickLaunchActions = quickLaunchActions;
        _toolCatalog = toolCatalog;
        _windowManagement = windowManagement;
    }

    /// <summary>快速启动磁贴，最后一项固定为加号。</summary>
    public ObservableCollection<QuickLaunchTileViewModel> QuickLaunchTiles { get; } = new();

    /// <summary>当前活动窗口快照（带应用图标）。</summary>
    public ObservableCollection<ManagedWindowViewModel> ActiveWindows { get; } = new();

    /// <summary>最近使用（工具 + 应用，合并按时间排序）。</summary>
    public ObservableCollection<RecentItemViewModel> RecentTools { get; } = new();

    /// <summary>最近剪贴板历史。</summary>
    public ObservableCollection<ClipboardItemViewModel> RecentClipboardItems { get; } = new();

    /// <summary>首页数据变更事件，供页面在可见期间订阅以实时刷新。</summary>
    public event EventHandler? HomeDataChanged
    {
        add
        {
            _recentItemsRepository.Changed += value;
            _clipboardRepository.Changed += value;
            _quickLaunchRepository.Changed += value;
            _settings.Changed += value;
            _appIndex.Loaded += value;
        }
        remove
        {
            _recentItemsRepository.Changed -= value;
            _clipboardRepository.Changed -= value;
            _quickLaunchRepository.Changed -= value;
            _settings.Changed -= value;
            _appIndex.Loaded -= value;
        }
    }

    /// <summary>首页最近使用区可见行数。</summary>
    public int RecentToolsVisibleRows => _settings.RecentToolsVisibleRows;

    /// <summary>首页快速启动区可见行数。</summary>
    public int QuickLaunchVisibleRows => _settings.QuickLaunchVisibleRows;

    /// <summary>是否无任何活动窗口（用于空状态占位的可见性绑定）。</summary>
    public bool HasNoActiveWindows => ActiveWindows.Count == 0;

    /// <summary>是否无任何最近使用记录。</summary>
    public bool HasNoTools => RecentTools.Count == 0;

    /// <summary>是否展示首页剪贴板历史区块。</summary>
    public bool ShowClipboardHistory => _settings.ShowHomeClipboardHistory;

    /// <summary>是否无任何最近剪贴板历史。</summary>
    public bool HasNoClipboardItems => RecentClipboardItems.Count == 0;

    /// <summary>
    /// 重新拉取数据并刷新集合。须在 UI 线程调用。
    /// </summary>
    public void Refresh()
    {
        RefreshQuickLaunch();

        // 合并工具和应用两种类型的最近使用记录，按时间倒序取上限。
        RecentTools.Clear();
        var fetchLimit = 40; // 内部多取，MaxHeight 用行数控制可见区域
        var merged = _recentItemsRepository.GetRecent(RecentItemKind.Tool, fetchLimit)
            .Where(r => _toolCatalog.FindById(r.Key) is not null)
            .Where(r => !r.Key.Equals("command.palette", StringComparison.OrdinalIgnoreCase))
            .Concat(_recentItemsRepository.GetRecent(RecentItemKind.App, fetchLimit))
            .OrderByDescending(r => r.LastUsedUtc)
            .Take(fetchLimit)
            .ToList();
        foreach (var item in merged)
        {
            RecentTools.Add(new RecentItemViewModel(item, GetRecentUsageIcon(item)));
        }

        RecentClipboardItems.Clear();
        if (_settings.ShowHomeClipboardHistory)
        {
            foreach (var item in _clipboardRepository.Get(null, kind: null, keyword: null, limit: ClipboardDisplayLimit))
            {
                RecentClipboardItems.Add(new ClipboardItemViewModel(item));
            }
        }
    }

    public void RefreshActiveWindows(nint ownerHwnd)
    {
        var snapshot = _windowManagement.ReadSnapshot(ownerHwnd);
        var next = snapshot.Windows
            .Select(window => new ManagedWindowViewModel(window, WindowIconProvider.GetIcon(window.ProcessPath)))
            .ToArray();

        for (var i = ActiveWindows.Count - 1; i >= 0; i--)
        {
            if (!next.Any(window => window.Hwnd == ActiveWindows[i].Hwnd))
            {
                ActiveWindows.RemoveAt(i);
            }
        }

        for (var i = 0; i < next.Length; i++)
        {
            var existingIndex = IndexOfActiveWindow(next[i].Hwnd);
            if (existingIndex < 0)
            {
                ActiveWindows.Insert(Math.Min(i, ActiveWindows.Count), next[i]);
                continue;
            }

            if (!ActiveWindows[existingIndex].HasSameDisplay(next[i].Model))
            {
                ActiveWindows[existingIndex] = next[i];
            }

            if (existingIndex != i && i < ActiveWindows.Count)
            {
                ActiveWindows.Move(existingIndex, i);
            }
        }
    }

    public void MaterializeQuickLaunchIcons(DispatcherQueue dispatcher)
    {
        _appIndex.MaterializeIcons(dispatcher);
    }

    public long AddQuickLaunchItem(QuickLaunchItemKind kind, string title, string target)
    {
        return _quickLaunchRepository.AddOrUpdate(new QuickLaunchItem(
            Id: 0,
            Kind: kind,
            Title: title,
            Target: target,
            SortOrder: 0,
            CreatedUtc: DateTime.UtcNow));
    }

    public void DeleteQuickLaunchItem(QuickLaunchTileViewModel tile)
    {
        if (tile.Model is not null)
        {
            _quickLaunchRepository.Delete(tile.Model.Id);
        }
    }

    public bool LaunchQuickLaunchItem(QuickLaunchTileViewModel tile)
    {
        if (tile.Model is null)
        {
            return false;
        }

        var launched = _quickLaunchActions.Launch(tile.Model);
        if (launched && _settings.TrackRecentTools)
        {
            TouchQuickLaunchRecentUsage(tile.Model);
        }

        return launched;
    }

    public bool OpenQuickLaunchLocation(QuickLaunchTileViewModel tile)
        => tile.Model is not null && _quickLaunchActions.OpenLocation(tile.Model);

    public bool RunQuickLaunchAsAdmin(QuickLaunchTileViewModel tile)
        => tile.Model is not null && _quickLaunchActions.RunAsAdmin(tile.Model);

    public bool CanOpenQuickLaunchLocation(QuickLaunchTileViewModel tile)
        => tile.Model is not null && _quickLaunchActions.CanOpenLocation(tile.Model.Kind, tile.Model.Target);

    public bool CanRunQuickLaunchAsAdmin(QuickLaunchTileViewModel tile)
        => tile.Model is not null && _quickLaunchActions.CanRunAsAdmin(tile.Model.Target);

    private void TouchQuickLaunchRecentUsage(QuickLaunchItem item)
    {
        _recentItemsRepository.Touch(new RecentItem(
            RecentItemKind.App,
            item.Target,
            item.Title,
            GetQuickLaunchRecentSubTitle(item),
            GetQuickLaunchGlyph(item.Kind),
            DateTime.UtcNow));
    }

    private void RefreshQuickLaunch()
    {
        QuickLaunchTiles.Clear();
        foreach (var item in _quickLaunchRepository.GetAll())
        {
            QuickLaunchTiles.Add(new QuickLaunchTileViewModel(item, GetQuickLaunchIcon(item)));
        }

        QuickLaunchTiles.Add(QuickLaunchTileViewModel.AddTile());
    }

    private BitmapImage? GetQuickLaunchIcon(QuickLaunchItem item)
    {
        if (item.Kind == QuickLaunchItemKind.App)
        {
            if (QuickLaunchActionService.IsFileSystemPath(item.Target))
            {
                return WindowIconProvider.GetIcon(item.Target);
            }

            foreach (var app in _appIndex.Apps)
            {
                if (app.AppUserModelId.Equals(item.Target, StringComparison.OrdinalIgnoreCase))
                {
                    return app.IconImage;
                }
            }

            return null;
        }

        if (item.Kind == QuickLaunchItemKind.File)
        {
            return WindowIconProvider.GetIcon(item.Target);
        }

        return null;
    }

    private static string GetQuickLaunchRecentSubTitle(QuickLaunchItem item)
        => item.Kind switch
        {
            QuickLaunchItemKind.App => QuickLaunchActionService.IsFileSystemPath(item.Target) ? "应用" : "应用",
            QuickLaunchItemKind.File => "文件",
            QuickLaunchItemKind.Folder => "文件夹",
            _ => string.Empty
        };

    private static string GetQuickLaunchGlyph(QuickLaunchItemKind kind)
        => kind switch
        {
            QuickLaunchItemKind.Folder => "\uE8B7",
            QuickLaunchItemKind.File => "\uE8A5",
            QuickLaunchItemKind.App => "\uECAA",
            _ => "\uECAA"
        };

    private BitmapImage? GetRecentUsageIcon(RecentItem item)
    {
        if (item.Kind != RecentItemKind.App)
        {
            return null;
        }

        if (QuickLaunchActionService.IsFileSystemPath(item.Key))
        {
            return WindowIconProvider.GetIcon(item.Key);
        }

        foreach (var app in _appIndex.Apps)
        {
            if (app.AppUserModelId.Equals(item.Key, StringComparison.OrdinalIgnoreCase))
            {
                return app.IconImage;
            }
        }

        return null;
    }

    public bool LaunchRecentApp(RecentItem item)
        => item.Kind == RecentItemKind.App && _quickLaunchActions.LaunchAppTarget(item.Key);

    public bool ActivateActiveWindow(ManagedWindowViewModel item)
        => _windowManagement.ActivateWindow(item.Model).Success;

    /// <summary>把首页剪贴板条目写回系统剪贴板。</summary>
    public void CopyClipboardItem(ClipboardItemViewModel item) => _clipboardWriter.Copy(item.Model);

    private int IndexOfActiveWindow(nint hwnd)
    {
        for (var i = 0; i < ActiveWindows.Count; i++)
        {
            if (ActiveWindows[i].Hwnd == hwnd)
            {
                return i;
            }
        }

        return -1;
    }
}
