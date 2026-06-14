using System;
using Microsoft.UI.Xaml;

namespace EdgeKit.App.Commands;

/// <summary>命令执行时需要由抽屉窗口提供的宿主能力。</summary>
public sealed record CommandExecutionContext(
    XamlRoot XamlRoot,
    Action<string> SelectTool,
    Action HideDrawer,
    Action FocusSearch,
    Action ClearSearch,
    Func<bool> IsPinned,
    Action<bool> SetPinned,
    Func<bool> IsMenuExpanded,
    Action<bool> SetMenuExpanded);
