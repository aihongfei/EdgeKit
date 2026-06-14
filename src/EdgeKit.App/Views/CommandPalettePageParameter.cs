using System;
using System.Threading.Tasks;
using EdgeKit.App.Commands;
using EdgeKit.Core.Commands;
using EdgeKit.Core.Recent;

namespace EdgeKit.App.Views;

internal sealed record CommandPalettePageParameter(
    CommandRegistry Registry,
    ICustomCommandRepository CustomCommands,
    IRecentItemsRepository RecentItems,
    Func<CommandDescriptor, Task> ExecuteCommand,
    Action HideDrawer);
