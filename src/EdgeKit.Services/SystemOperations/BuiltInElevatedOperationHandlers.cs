using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using EdgeKit.Services.Diagnostics;

namespace EdgeKit.Services.SystemOperations;

public static class ElevatedOperationIds
{
    public const string FileWrite = "file.write";
    public const string FilePatch = "file.patch";
    public const string FileDeleteRecycle = "file.deleteRecycle";
    public const string HostsSave = "hosts.save";
    public const string EnvironmentVariableSave = "environmentVariable.save";
    public const string EnvironmentVariableRestore = "environmentVariable.restore";
    public const string FileLockAction = "fileLock.action";
    public const string ProcessKill = "process.kill";
    public const string PowerShellRun = "powershell.run";
}

public sealed class FileWriteElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.FileWrite;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<FileWritePayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("文件写入请求无效");
        var path = ElevatedOperationPath.Normalize(request.Path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (request.Append)
        {
            File.AppendAllText(path, request.Content, Encoding.UTF8);
            return ElevatedOperationResult.Ok("已通过管理员授权追加写入文件: " + path);
        }

        File.WriteAllText(path, request.Content, Encoding.UTF8);
        return ElevatedOperationResult.Ok("已通过管理员授权写入文件: " + path);
    }
}

public sealed class FilePatchElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.FilePatch;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<FilePatchPayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("文件替换请求无效");
        var path = ElevatedOperationPath.Normalize(request.Path);
        if (!File.Exists(path))
        {
            throw new ArgumentException("文件不存在: " + path);
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        var index = text.IndexOf(request.OldText, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new ArgumentException("未找到 oldText，未修改文件。");
        }

        if (text.IndexOf(request.OldText, index + request.OldText.Length, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("oldText 出现多次，请提供更精确的文本。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var updated = text.Replace(request.OldText, request.NewText, StringComparison.Ordinal);
        File.WriteAllText(path, updated, Encoding.UTF8);
        return ElevatedOperationResult.Ok("已通过管理员授权替换文件文本: " + path);
    }
}

public sealed class FileDeleteRecycleElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.FileDeleteRecycle;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<FileDeleteRecyclePayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("文件删除请求无效");
        var path = ElevatedOperationPath.Normalize(request.Path);
        var isDirectory = request.IsDirectory ?? Directory.Exists(path);
        if (isDirectory)
        {
            if (!Directory.Exists(path))
            {
                throw new ArgumentException("目录不存在: " + path);
            }

            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return ElevatedOperationResult.Ok("已通过管理员授权移入回收站: " + path);
        }

        if (!File.Exists(path))
        {
            throw new ArgumentException("文件不存在: " + path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return ElevatedOperationResult.Ok("已通过管理员授权移入回收站: " + path);
    }
}

public sealed class HostsSaveElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.HostsSave;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<HostsSavePayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("hosts 保存请求无效");
        cancellationToken.ThrowIfCancellationRequested();
        var result = HostsFileService.SaveAsAdministrator(request.Content);
        return result.Success
            ? ElevatedOperationResult.Ok(result.Message)
            : ElevatedOperationResult.Fail(result.Message, "hosts_save_failed");
    }
}

public sealed class EnvironmentVariableSaveElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.EnvironmentVariableSave;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<EnvironmentVariableSavePayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("环境变量保存请求无效");
        var result = EnvironmentVariableService.ApplyAsAdministrator(
            ParseTarget(request.Target),
            request.Name,
            request.Value,
            request.Delete,
            request.RestoreVariables);
        return result.Success
            ? ElevatedOperationResult.Ok(result.Message)
            : ElevatedOperationResult.Fail(result.Message, "environment_variable_failed");
    }

    private static EnvironmentVariableTarget ParseTarget(string value)
        => Enum.TryParse(value, ignoreCase: true, out EnvironmentVariableTarget target)
            && target is EnvironmentVariableTarget.User or EnvironmentVariableTarget.Machine
            ? target
            : throw new ArgumentException("环境变量目标无效: " + value);
}

public sealed class EnvironmentVariableRestoreElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.EnvironmentVariableRestore;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<EnvironmentVariableRestorePayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("环境变量恢复请求无效");
        var result = EnvironmentVariableService.RestoreBackupAsAdministrator(request.BackupPath);
        return result.Success
            ? ElevatedOperationResult.Ok(result.Message)
            : ElevatedOperationResult.Fail(result.Message, "environment_variable_restore_failed");
    }
}

public sealed class FileLockElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.FileLockAction;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<FileLockActionPayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("文件锁定操作请求无效");
        var service = new FileLockService();
        var result = service.ExecuteAsAdministrator(
            request.Action,
            request.TargetPath,
            request.IsDirectory,
            request.ProcessIds,
            request.Handles);
        return result.Success
            ? ElevatedOperationResult.Ok(result.Message)
            : ElevatedOperationResult.Fail(result.Message, "file_lock_action_failed");
    }
}

public sealed class PowerShellElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.PowerShellRun;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<PowerShellRunPayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("PowerShell 请求无效");
        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds <= 0 ? 30 : request.TimeoutSeconds, 1, 600);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(request.Command),
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : ElevatedOperationPath.Normalize(request.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PowerShell。");
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return ElevatedOperationResult.Fail("Shell 命令执行超时。", "timeout");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        var message = "管理员 PowerShell 已执行，ExitCode: " + process.ExitCode.ToString(CultureInfo.InvariantCulture);
        return process.ExitCode == 0
            ? ElevatedOperationResult.Ok(message, output, error, process.ExitCode)
            : ElevatedOperationResult.Fail(message, "non_zero_exit", process.ExitCode, output, error);
    }

    private static string EncodePowerShellCommand(string command)
    {
        var script = "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + command;
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }
}

public sealed class ProcessKillElevatedOperationHandler : IElevatedOperationHandler
{
    public string OperationId => ElevatedOperationIds.ProcessKill;

    public ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = payload.Deserialize<ProcessKillPayload>(ElevatedOperationJson.Options)
            ?? throw new ArgumentException("结束进程请求无效");
        if (request.ProcessId <= 0 || request.ProcessId == Environment.ProcessId || request.ProcessId == 4)
        {
            return ElevatedOperationResult.Fail("该进程不允许结束", "invalid_process");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.GetProcessById(request.ProcessId);
        var name = string.IsNullOrWhiteSpace(request.ProcessName) ? process.ProcessName : request.ProcessName;
        process.Kill(entireProcessTree: false);
        process.WaitForExit(2500);
        return ElevatedOperationResult.Ok($"已通过管理员授权结束 {name} (PID {request.ProcessId})");
    }
}

public sealed record FileWritePayload(string Path, string Content, bool Append);

public sealed record FilePatchPayload(string Path, string OldText, string NewText);

public sealed record FileDeleteRecyclePayload(string Path, bool? IsDirectory);

public sealed record HostsSavePayload(string Content);

public sealed record EnvironmentVariableSavePayload(
    string Target,
    string Name,
    string Value,
    bool Delete,
    IReadOnlyDictionary<string, string>? RestoreVariables);

public sealed record EnvironmentVariableRestorePayload(string BackupPath);

public sealed record FileLockActionPayload(
    string Action,
    string TargetPath,
    bool IsDirectory,
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<FileLockHandleSelection> Handles);

public sealed record PowerShellRunPayload(
    string Command,
    string WorkingDirectory,
    int TimeoutSeconds);

public sealed record ProcessKillPayload(int ProcessId, string ProcessName);

internal static class ElevatedOperationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

internal static class ElevatedOperationPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。");
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
    }
}
