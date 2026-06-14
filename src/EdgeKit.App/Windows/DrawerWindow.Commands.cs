using System;
using System.Threading.Tasks;
using EdgeKit.App.Commands;
using EdgeKit.Core.Commands;
using EdgeKit.Core.Recent;

namespace EdgeKit.App.Windows;

/// <summary>DrawerWindow 命令面板执行入口。</summary>
public sealed partial class DrawerWindow
{
    private async Task ExecuteCommandAsync(CommandDescriptor command)
    {
        var succeeded = await _commandExecutor.ExecuteAsync(command, CreateCommandExecutionContext());
        if (!succeeded)
        {
            return;
        }

        _recentItems.Touch(new RecentItem(
            RecentItemKind.Command,
            command.Id,
            command.Title,
            command.Category,
            command.Glyph,
            DateTime.UtcNow));
    }

    private CommandExecutionContext CreateCommandExecutionContext()
        => new(
            RootGrid.XamlRoot,
            SelectToolFromHome,
            HideDrawer,
            FocusSearchInput,
            ClearSearch,
            () => _isPinned,
            SetPinned,
            () => ToolNav.IsPaneOpen,
            expanded => ToolNav.IsPaneOpen = expanded);
}
