using EdgeKit.App.Views;
using EdgeKit.App.Windows.Navigation;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Tools;
using Microsoft.UI.Xaml.Controls;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 侧边菜单构建、选中路由与展开/收缩同步。</summary>
public sealed partial class DrawerWindow
{
    /// <summary>
    /// 根据工具注册表动态构建侧边菜单。委托给 <see cref="ToolNavigationBuilder"/>，
    /// 加工具只需在 ToolCatalog 注册，无需改此处代码。
    /// </summary>
    private void BuildMenu()
    {
        var result = ToolNavigationBuilder.Build(ToolNav, _shellViewModel);
        _menuItemTools = result.ItemToolMap;

        // 默认选中第一个工具。
        if (result.FirstTool is not null)
        {
            ToolNav.SelectedItem = result.FirstTool;
        }
    }

    private void OnToolSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item
            && _menuItemTools.TryGetValue(item, out var toolId))
        {
            NavigateToTool(toolId, recordRecent: true);
        }
    }

    private void NavigateToTool(string toolId, bool recordRecent)
    {
        _shellViewModel.SelectToolById(toolId);

        var tool = _shellViewModel.SelectedTool;
        if (tool is null)
        {
            return;
        }

        // 记录工具使用（受开关控制；首页本身不计入）。
        if (recordRecent && _settings.TrackRecentTools && toolId != "home.dashboard" && toolId != "command.palette")
        {
            _recentItems.Touch(new RecentItem(
                RecentItemKind.Tool,
                tool.Id,
                tool.Title,
                ToolCategoryInfo.GetTitle(tool.Category),
                tool.Glyph,
                System.DateTime.UtcNow));
        }

        // 路由：首页 → HomePage；设置中心 → 设置页；文本工具 → 文本工作台；系统工具 → 诊断工作台；其余 → 统一占位页。
        if (toolId == "home.dashboard")
        {
            ContentFrame.Navigate(
                typeof(HomePage),
                new HomePageParameter(_homeViewModel, SelectToolFromHome, _hwnd, FocusSearchInput));
        }
        else if (toolId == "settings.center")
        {
            ContentFrame.Navigate(typeof(SettingsPage), _settings);
        }
        else if (toolId == "clipboard.history")
        {
            ContentFrame.Navigate(typeof(ClipboardHistoryPage), _clipboardViewModel);
        }
        else if (toolId == "command.palette")
        {
            ContentFrame.Navigate(
                typeof(CommandPalettePage),
                new CommandPalettePageParameter(
                    _commandRegistry,
                    _customCommands,
                    _recentItems,
                    ExecuteCommandAsync,
                    HideDrawer));
        }
        else if (toolId == "agent.chat")
        {
            ContentFrame.Navigate(typeof(AgentChatPage), new AgentChatPageParameter(_agentService));
        }
        else if (toolId == "task.board")
        {
            ContentFrame.Navigate(typeof(TaskBoardPage), new TaskBoardPageParameter(_taskBoardViewModel));
        }
        else if (toolId == "system.dashboard")
        {
            ContentFrame.Navigate(typeof(SystemDashboardPage), new SystemDashboardPageParameter(_systemDashboardViewModel));
        }
        else if (toolId == "text.translate")
        {
            ContentFrame.Navigate(typeof(TranslateToolsPage), new TranslateToolsPageParameter(_agentService, _youdaoService, _hwnd));
        }
        else if (tool.Category == ToolCategory.Text)
        {
            ContentFrame.Navigate(typeof(TextToolsPage), new TextToolsPageParameter(toolId, _textTools, _hwnd));
        }
        else if (toolId == "image.convert")
        {
            ContentFrame.Navigate(typeof(ImageConvertPage), new ImageConvertPageParameter(_imageTools, _hwnd));
        }
        else if (tool.Category == ToolCategory.System)
        {
            ContentFrame.Navigate(
                typeof(SystemToolsPage),
                new SystemToolsPageParameter(
                    toolId,
                    _hwnd,
                    _systemDiagnostics,
                    _hostsFileService,
                    _environmentVariables,
                    _windowManagement,
                    _fileLocks));
        }
        else
        {
            ContentFrame.Navigate(typeof(ToolPlaceholderPage), tool);
        }
    }

    /// <summary>
    /// 首页点击最近工具时的回调：选中左侧菜单对应项，触发常规导航。
    /// </summary>
    private void SelectToolFromHome(string toolId)
    {
        var visibleToolId = GetVisibleToolId(toolId);
        foreach (var pair in _menuItemTools)
        {
            if (pair.Value == visibleToolId)
            {
                ToolNav.SelectedItem = pair.Key;
                if (visibleToolId != toolId)
                {
                    NavigateToTool(toolId, recordRecent: true);
                }

                return;
            }
        }
    }

    private static string GetVisibleToolId(string toolId)
        => toolId is "text.encode" or "json.format" or "json.tree" ? "text.tools"
            : toolId;

    private void OnPaneOpening(NavigationView sender, object args)
    {
        if (!_settings.MenuExpanded)
        {
            _settings.MenuExpanded = true;
            _settings.Save();
        }

        // 展开：窗口加宽 pane 多出的部分，内容区不被压缩。
        // 抽屉已展开时平滑过渡，否则（在屏幕外/滑动中）瞬时设置避免与 X 动画打架。
        ClampCurrentDrawerWidth(expanded: true, save: true);
        if (_state == DrawerState.Shown)
        {
            AnimateWidth(EffectiveWidth(expanded: true));
        }
        else
        {
            ApplyWidth(expanded: true);
        }
    }

    private void OnPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (_settings.MenuExpanded)
        {
            _settings.MenuExpanded = false;
            _settings.Save();
        }

        // 收缩：窗口恢复基础宽度。
        ClampCurrentDrawerWidth(expanded: false, save: true);
        if (_state == DrawerState.Shown)
        {
            AnimateWidth(EffectiveWidth(expanded: false));
        }
        else
        {
            ApplyWidth(expanded: false);
        }
    }
}
