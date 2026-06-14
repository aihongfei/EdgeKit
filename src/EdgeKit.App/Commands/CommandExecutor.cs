using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EdgeKit.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace EdgeKit.App.Commands;

/// <summary>统一命令执行器。</summary>
public sealed class CommandExecutor
{
    public async Task<bool> ExecuteAsync(CommandDescriptor command, CommandExecutionContext context)
    {
        if (command.RequiresConfirmation && !await ConfirmAsync(command, context.XamlRoot))
        {
            return false;
        }

        try
        {
            return command.Kind switch
            {
                CommandKind.BuiltInAction => ExecuteBuiltIn(command.Target, context),
                CommandKind.WindowsUri => LaunchUri(command.Target, context),
                CommandKind.Path => LaunchPath(command.Target, command.Arguments, command.RunAsAdministrator, context),
                CommandKind.Process => LaunchProcess(
                    command.Target,
                    command.Arguments,
                    command.WorkingDirectory,
                    command.RunAsAdministrator,
                    hideDrawer: true,
                    context),
                CommandKind.Custom => ExecuteCustom(command, context),
                _ => false
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "命令执行失败 id={Id} title={Title}", command.Id, command.Title);
            return false;
        }
    }

    private static bool ExecuteBuiltIn(string target, CommandExecutionContext context)
    {
        if (target.StartsWith("navigate:", StringComparison.OrdinalIgnoreCase))
        {
            context.SelectTool(target["navigate:".Length..]);
            return true;
        }

        switch (target)
        {
            case "drawer:hide":
                context.HideDrawer();
                return true;
            case "drawer:toggle-pin":
                context.SetPinned(!context.IsPinned());
                return true;
            case "drawer:toggle-menu":
                context.SetMenuExpanded(!context.IsMenuExpanded());
                return true;
            case "search:focus":
                context.FocusSearch();
                return true;
            case "search:clear":
                context.ClearSearch();
                context.FocusSearch();
                return true;
            default:
                return false;
        }
    }

    private static bool ExecuteCustom(CommandDescriptor command, CommandExecutionContext context)
    {
        var target = Expand(command.Target);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (LooksLikeUri(target))
        {
            return LaunchUri(target, context);
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            return LaunchPath(target, command.Arguments, command.RunAsAdministrator, context);
        }

        return LaunchProcess(
            "cmd.exe",
            "/c " + QuoteForCmd(target, command.Arguments),
            command.WorkingDirectory,
            command.RunAsAdministrator,
            hideDrawer: true,
            context);
    }

    private static bool LaunchUri(string uri, CommandExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = Expand(uri),
            UseShellExecute = true
        });
        context.HideDrawer();
        return true;
    }

    private static bool LaunchPath(
        string path,
        string arguments,
        bool runAsAdministrator,
        CommandExecutionContext context)
    {
        path = Expand(path).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var info = new ProcessStartInfo
        {
            FileName = path,
            Arguments = Expand(arguments),
            UseShellExecute = true
        };
        if (runAsAdministrator)
        {
            info.Verb = "runas";
        }

        Process.Start(info);
        context.HideDrawer();
        return true;
    }

    private static bool LaunchProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        bool runAsAdministrator,
        bool hideDrawer,
        CommandExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var info = new ProcessStartInfo
        {
            FileName = Expand(fileName),
            Arguments = Expand(arguments),
            UseShellExecute = true
        };

        var directory = Expand(workingDirectory);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            info.WorkingDirectory = directory;
        }

        if (runAsAdministrator)
        {
            info.Verb = "runas";
        }

        Process.Start(info);
        if (hideDrawer)
        {
            context.HideDrawer();
        }

        return true;
    }

    private static async Task<bool> ConfirmAsync(CommandDescriptor command, XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = "确认执行命令",
            Content = $"即将执行「{command.Title}」。这个操作可能影响当前系统状态。",
            PrimaryButtonText = "执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = ElementTheme.Dark
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static bool LooksLikeUri(string value)
        => value.Contains(':', StringComparison.Ordinal)
            && !LooksLikeFilePath(value);

    private static bool LooksLikeFilePath(string value)
        => value.Length > 2
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/')
            || value.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Expand(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Environment.ExpandEnvironmentVariables(value.Trim());

    private static string QuoteForCmd(string commandText, string arguments)
    {
        var command = commandText.Trim();
        var args = Expand(arguments).Trim();
        return string.IsNullOrWhiteSpace(args) ? command : command + " " + args;
    }
}
