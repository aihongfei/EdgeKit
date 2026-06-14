using System;
using EdgeKit.App.ViewModels;

namespace EdgeKit.App.Views;

/// <summary>
/// 导航到 <see cref="HomePage"/> 时传入的参数：携带首页视图模型与"选中工具"回调。
/// 回调由抽屉窗口提供，用于在首页点击最近工具时同步选中左侧菜单对应项并切换内容区。
/// </summary>
/// <param name="ViewModel">首页视图模型。</param>
/// <param name="SelectTool">按工具 Id 选中菜单项的回调。</param>
/// <param name="WindowHandle">抽屉窗口句柄，用于 WinUI 文件/文件夹选择器初始化。</param>
/// <param name="FocusSearch">把键盘焦点还给抽屉搜索框的回调。</param>
public sealed record HomePageParameter(
    HomeViewModel ViewModel,
    Action<string> SelectTool,
    nint WindowHandle,
    Action FocusSearch);
