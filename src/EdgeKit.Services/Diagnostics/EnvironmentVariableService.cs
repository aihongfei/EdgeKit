using System.Collections;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EdgeKit.Native;
using EdgeKit.Services.SystemOperations;

namespace EdgeKit.Services.Diagnostics;

/// <summary>Reads and edits user / machine environment variables.</summary>
public sealed class EnvironmentVariableService
{
    public const string ElevatedSaveArgument = "--edgekit-save-env";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ElevatedOperationService? _elevation;

    public EnvironmentVariableService()
    {
    }

    public EnvironmentVariableService(ElevatedOperationService elevation)
    {
        _elevation = elevation;
    }

    public EnvironmentVariableSnapshot ReadSnapshot()
    {
        EnsureAppDirectories();
        var entries = ReadEntries(EnvironmentVariableTarget.User)
            .Concat(ReadEntries(EnvironmentVariableTarget.Machine))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Target)
            .ToArray();

        return new EnvironmentVariableSnapshot(
            DateTime.Now,
            IsAdministrator(),
            entries,
            GetBackups());
    }

    public IReadOnlyList<EnvironmentPathItem> AnalyzePath(string value)
    {
        var parts = value.Split(';', StringSplitOptions.None);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<EnvironmentPathItem>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var raw = parts[i];
            var trimmed = raw.Trim();
            var normalized = NormalizePath(trimmed);
            var isEmpty = string.IsNullOrWhiteSpace(trimmed);
            var isDuplicate = !isEmpty && !seen.Add(normalized);
            var exists = !isEmpty && (Directory.Exists(Environment.ExpandEnvironmentVariables(trimmed))
                || File.Exists(Environment.ExpandEnvironmentVariables(trimmed)));
            var isTooLong = trimmed.Length > 240;

            items.Add(new EnvironmentPathItem(
                i + 1,
                raw,
                isEmpty,
                isDuplicate,
                !isEmpty && !exists,
                isTooLong));
        }

        return items;
    }

    public async Task<ToolActionResult> SaveAsync(
        EnvironmentVariableTarget target,
        string name,
        string value,
        CancellationToken cancellationToken = default)
        => await SaveAsyncCore(target, name, value, delete: false, cancellationToken).ConfigureAwait(false);

    public async Task<ToolActionResult> DeleteAsync(
        EnvironmentVariableTarget target,
        string name,
        CancellationToken cancellationToken = default)
        => await SaveAsyncCore(target, name, string.Empty, delete: true, cancellationToken).ConfigureAwait(false);

    public async Task<ToolActionResult> RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
        => await RestoreBackupAsyncCore(backupPath, cancellationToken).ConfigureAwait(false);

    public string BuildReport(IReadOnlyList<EnvironmentVariableEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 环境变量");
        builder.AppendLine($"刷新时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"变量数量: {entries.Count}");
        foreach (var entry in entries)
        {
            builder.AppendLine(entry.ToReportText());
        }

        return builder.ToString().TrimEnd();
    }

    public static int ExecuteElevatedSaveCommand(string requestPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(requestPath);
            var pending = Path.GetFullPath(PendingDirectory);
            if (!fullPath.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                WriteResult(fullPath, "请求路径不在 EdgeKit pending-env 目录内");
                return 2;
            }

            if (!File.Exists(fullPath))
            {
                WriteResult(fullPath, "环境变量保存请求不存在");
                return 3;
            }

            var request = JsonSerializer.Deserialize<EnvironmentVariableWriteRequest>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions);
            if (request is null || !TryParseTarget(request.Target, out var target))
            {
                WriteResult(fullPath, "环境变量保存请求无效");
                return 4;
            }

            var result = ApplyAsAdministrator(
                target,
                request.Name,
                request.Value,
                request.Delete,
                request.RestoreVariables);
            WriteResult(fullPath, result.Message);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException)
        {
            try
            {
                WriteResult(requestPath, ex.Message);
            }
            catch
            {
                // Best effort only.
            }

            return 1;
        }
    }

    private async Task<ToolActionResult> SaveAsyncCore(
        EnvironmentVariableTarget target,
        string name,
        string value,
        bool delete,
        CancellationToken cancellationToken)
    {
        if (!ValidateName(name, out var message))
        {
            return new ToolActionResult(false, message);
        }

        if (target == EnvironmentVariableTarget.Machine && !IsAdministrator())
        {
            if (_elevation is null)
            {
                return new ToolActionResult(false, "需要管理员权限，但提权服务不可用");
            }

            var result = await _elevation.RunElevatedAsync(
                ElevatedOperationIds.EnvironmentVariableSave,
                new EnvironmentVariableSavePayload(
                    target.ToString(),
                    name.Trim(),
                    value,
                    delete,
                    null),
                "diagnostics:environment",
                delete ? "删除系统环境变量 " + name.Trim() : "保存系统环境变量 " + name.Trim(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ToToolActionResult();
        }

        return await Task.Run(
            () => ApplyAsAdministrator(target, name.Trim(), value, delete, null),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolActionResult> RestoreBackupAsyncCore(string backupPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return new ToolActionResult(false, "请选择一个环境变量备份");
        }

        var fullPath = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(BackupDirectory);
        if (!fullPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).EndsWith(".env.json", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolActionResult(false, "备份路径不在 EdgeKit 环境变量备份目录内");
        }

        if (!File.Exists(fullPath))
        {
            return new ToolActionResult(false, "环境变量备份不存在");
        }

        try
        {
            var backup = JsonSerializer.Deserialize<EnvironmentVariableBackupPayload>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions);
            if (backup is null || !TryParseTarget(backup.Target, out var target))
            {
                return new ToolActionResult(false, "环境变量备份无效");
            }

            if (target == EnvironmentVariableTarget.Machine && !IsAdministrator())
            {
                if (_elevation is null)
                {
                    return new ToolActionResult(false, "需要管理员权限，但提权服务不可用");
                }

                var result = await _elevation.RunElevatedAsync(
                    ElevatedOperationIds.EnvironmentVariableSave,
                    new EnvironmentVariableSavePayload(
                        target.ToString(),
                        string.Empty,
                        string.Empty,
                        false,
                        backup.Variables),
                    "diagnostics:environment",
                    "恢复系统环境变量备份",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return result.ToToolActionResult();
            }

            return await Task.Run(
                () => ApplyAsAdministrator(target, string.Empty, string.Empty, delete: false, backup.Variables),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return new ToolActionResult(false, "恢复失败：" + ex.Message);
        }
    }

    public static ToolActionResult ApplyAsAdministrator(
        EnvironmentVariableTarget target,
        string name,
        string value,
        bool delete,
        IReadOnlyDictionary<string, string>? restoreVariables)
    {
        if (restoreVariables is null && !ValidateName(name, out var message))
        {
            return new ToolActionResult(false, message);
        }

        try
        {
            BackupCurrent(target);
            if (restoreVariables is not null)
            {
                RestoreVariables(target, restoreVariables);
                BroadcastEnvironmentChanged();
                return new ToolActionResult(true, "环境变量备份已恢复，新进程生效，已运行进程可能需重启");
            }

            Environment.SetEnvironmentVariable(name.Trim(), delete ? null : value, target);
            BroadcastEnvironmentChanged();
            return new ToolActionResult(
                true,
                (delete ? "环境变量已删除" : "环境变量已保存") + "，新进程生效，已运行进程可能需重启");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return new ToolActionResult(false, "保存失败：" + ex.Message);
        }
    }

    public static ToolActionResult RestoreBackupAsAdministrator(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return new ToolActionResult(false, "请选择一个环境变量备份");
        }

        var fullPath = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(BackupDirectory);
        if (!fullPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).EndsWith(".env.json", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolActionResult(false, "备份路径不在 EdgeKit 环境变量备份目录内");
        }

        if (!File.Exists(fullPath))
        {
            return new ToolActionResult(false, "环境变量备份不存在");
        }

        try
        {
            var backup = JsonSerializer.Deserialize<EnvironmentVariableBackupPayload>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions);
            if (backup is null || !TryParseTarget(backup.Target, out var target))
            {
                return new ToolActionResult(false, "环境变量备份无效");
            }

            return ApplyAsAdministrator(target, string.Empty, string.Empty, delete: false, backup.Variables);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return new ToolActionResult(false, "恢复失败：" + ex.Message);
        }
    }

    private static IReadOnlyList<EnvironmentVariableEntry> ReadEntries(EnvironmentVariableTarget target)
    {
        var variables = Environment.GetEnvironmentVariables(target);
        var entries = new List<EnvironmentVariableEntry>(variables.Count);
        foreach (DictionaryEntry item in variables)
        {
            var name = item.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            entries.Add(new EnvironmentVariableEntry(
                name,
                item.Value?.ToString() ?? string.Empty,
                target));
        }

        return entries;
    }

    private static Dictionary<string, string> ReadVariables(EnvironmentVariableTarget target)
        => ReadEntries(target).ToDictionary(e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);

    private static void BackupCurrent(EnvironmentVariableTarget target)
    {
        EnsureAppDirectories();
        var payload = new EnvironmentVariableBackupPayload(
            target.ToString(),
            DateTime.Now,
            ReadVariables(target));
        var path = Path.Combine(
            BackupDirectory,
            $"env-{target.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.env.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }

    private static EnvironmentBackupInfo[] GetBackups()
    {
        EnsureAppDirectories();
        return Directory.EnumerateFiles(BackupDirectory, "*.env.json")
            .Select(path =>
            {
                var target = EnvironmentVariableTarget.User;
                try
                {
                    var payload = JsonSerializer.Deserialize<EnvironmentVariableBackupPayload>(
                        File.ReadAllText(path, Encoding.UTF8),
                        JsonOptions);
                    if (payload is not null && TryParseTarget(payload.Target, out var parsed))
                    {
                        target = parsed;
                    }
                }
                catch
                {
                    // Ignore broken backup metadata; the restore path will validate it.
                }

                return new EnvironmentBackupInfo(
                    path,
                    Path.GetFileName(path),
                    File.GetLastWriteTime(path),
                    target);
            })
            .OrderByDescending(b => b.CreatedAt)
            .Take(30)
            .ToArray();
    }

    private static void RestoreVariables(EnvironmentVariableTarget target, IReadOnlyDictionary<string, string> variables)
    {
        var existing = ReadVariables(target);
        foreach (var name in existing.Keys)
        {
            if (!variables.ContainsKey(name))
            {
                Environment.SetEnvironmentVariable(name, null, target);
            }
        }

        foreach (var item in variables)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value, target);
        }
    }

    private static void BroadcastEnvironmentChanged()
        => NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST,
            NativeMethods.WM_SETTINGCHANGE,
            nint.Zero,
            "Environment",
            NativeMethods.SMTO_ABORTIFHUNG,
            3000,
            out _);

    private static bool ValidateName(string name, out string message)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "变量名不能为空";
            return false;
        }

        if (name.Contains('='))
        {
            message = "变量名不能包含等号";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryParseTarget(string value, out EnvironmentVariableTarget target)
        => Enum.TryParse(value, ignoreCase: true, out target)
            && target is EnvironmentVariableTarget.User or EnvironmentVariableTarget.Machine;

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static void EnsureAppDirectories()
    {
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(PendingDirectory);
    }

    private static void WriteResult(string requestPath, string message)
        => File.WriteAllText(GetResultPath(requestPath), message, Encoding.UTF8);

    private static string GetResultPath(string requestPath)
        => requestPath + ".result";

    private static string EdgeKitDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeKit");

    private static string BackupDirectory
        => Path.Combine(EdgeKitDirectory, "env-backups");

    private static string PendingDirectory
        => Path.Combine(EdgeKitDirectory, "pending-env");

    private sealed record EnvironmentVariableWriteRequest(
        string Target,
        string Name,
        string Value,
        bool Delete,
        IReadOnlyDictionary<string, string>? RestoreVariables);

    private sealed record EnvironmentVariableBackupPayload(
        string Target,
        DateTime CreatedAt,
        IReadOnlyDictionary<string, string> Variables);
}

public sealed record EnvironmentVariableSnapshot(
    DateTime RefreshedAt,
    bool IsAdministrator,
    IReadOnlyList<EnvironmentVariableEntry> Entries,
    IReadOnlyList<EnvironmentBackupInfo> Backups)
{
    public string SummaryText
        => $"{Entries.Count(e => e.Target == EnvironmentVariableTarget.User)} 个用户变量 · {Entries.Count(e => e.Target == EnvironmentVariableTarget.Machine)} 个系统变量 · {(IsAdministrator ? "管理员" : "普通权限")} · {RefreshedAt:HH:mm:ss}";
}

public sealed record EnvironmentVariableEntry(
    string Name,
    string Value,
    EnvironmentVariableTarget Target)
{
    public string TargetText => Target == EnvironmentVariableTarget.Machine ? "系统" : "用户";

    public string ValuePreview
    {
        get
        {
            var compact = Value.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
            return compact.Length <= 140 ? compact : compact[..140] + "...";
        }
    }

    public bool IsPathVariable => string.Equals(Name, "Path", StringComparison.OrdinalIgnoreCase);

    public string Detail => $"{TargetText} · {ValuePreview}";

    public string ToReportText() => $"[{TargetText}] {Name}={Value}";
}

public sealed record EnvironmentPathItem(
    int Index,
    string Value,
    bool IsEmpty,
    bool IsDuplicate,
    bool Missing,
    bool IsTooLong)
{
    public string IndexText => Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DisplayValue => string.IsNullOrWhiteSpace(Value) ? "(空项)" : Value;

    public string IssueText
    {
        get
        {
            var issues = new List<string>();
            if (IsEmpty)
            {
                issues.Add("空项");
            }

            if (IsDuplicate)
            {
                issues.Add("重复");
            }

            if (Missing)
            {
                issues.Add("路径不存在");
            }

            if (IsTooLong)
            {
                issues.Add("过长");
            }

            return issues.Count == 0 ? "正常" : string.Join(" / ", issues);
        }
    }
}

public sealed record EnvironmentBackupInfo(
    string Path,
    string Name,
    DateTime CreatedAt,
    EnvironmentVariableTarget Target)
{
    public string TargetText => Target == EnvironmentVariableTarget.Machine ? "系统变量" : "用户变量";

    public string DisplayText => $"{TargetText} · {Name} · {CreatedAt:yyyy-MM-dd HH:mm:ss}";
}
