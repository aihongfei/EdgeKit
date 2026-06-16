using System.Globalization;
using System.Net;
using System.Security;
using System.Security.Principal;
using System.Text;
using EdgeKit.Native;
using EdgeKit.Services.SystemOperations;

namespace EdgeKit.Services.Diagnostics;

/// <summary>
/// Reads, validates, backs up and saves the Windows hosts file.
/// </summary>
public sealed class HostsFileService
{
    public const string ElevatedSaveArgument = "--edgekit-save-hosts";

    private static readonly Encoding HostsEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ElevatedOperationService? _elevation;

    public HostsFileService()
    {
    }

    public HostsFileService(ElevatedOperationService elevation)
    {
        _elevation = elevation;
    }

    public HostsFileSnapshot ReadSnapshot()
    {
        EnsureAppDirectories();
        var content = File.Exists(HostsPath)
            ? File.ReadAllText(HostsPath, Encoding.UTF8)
            : string.Empty;

        return BuildSnapshot(content);
    }

    public HostsFileSnapshot BuildSnapshot(string content)
        => new(
            HostsPath,
            DateTime.Now,
            IsAdministrator(),
            content,
            ParseContent(content),
            ValidateContent(content),
            GetBackups());

    public IReadOnlyList<HostsLineInfo> ParseContent(string content)
    {
        var lines = SplitLines(content);
        var result = new List<HostsLineInfo>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            result.Add(ParseLine(i + 1, lines[i]));
        }

        return result;
    }

    public IReadOnlyList<HostsIssue> ValidateContent(string content)
    {
        var rows = ParseContent(content);
        var issues = new List<HostsIssue>();
        var enabledHosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.IsIgnorable)
            {
                continue;
            }

            if (!row.IsEntry)
            {
                issues.Add(new HostsIssue(row.LineNumber, "无法识别", "非注释行不是有效 hosts 记录"));
                continue;
            }

            if (!row.IsEnabled)
            {
                continue;
            }

            foreach (var host in row.HostnamesList)
            {
                if (enabledHosts.TryGetValue(host, out var firstLine))
                {
                    issues.Add(new HostsIssue(row.LineNumber, "重复域名", $"{host} 已在第 {firstLine} 行出现"));
                }
                else
                {
                    enabledHosts[host] = row.LineNumber;
                }

                if (host.Contains(' ') || host.Contains('\t') || host.Contains('/'))
                {
                    issues.Add(new HostsIssue(row.LineNumber, "域名可疑", host));
                }
            }
        }

        return issues;
    }

    public string ToggleLine(string content, int lineNumber)
    {
        var lines = SplitLines(content);
        if (lineNumber < 1 || lineNumber > lines.Length)
        {
            return content;
        }

        var index = lineNumber - 1;
        var line = lines[index];
        var info = ParseLine(lineNumber, line);
        if (!info.CanToggle)
        {
            return content;
        }

        if (info.IsEnabled)
        {
            lines[index] = "# " + line;
        }
        else
        {
            var hashIndex = line.IndexOf('#');
            if (hashIndex >= 0)
            {
                var beforeHash = line[..hashIndex];
                var afterHash = line[(hashIndex + 1)..];
                if (afterHash.StartsWith(' '))
                {
                    afterHash = afterHash[1..];
                }

                lines[index] = beforeHash + afterHash;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public async Task<HostsSaveResult> SaveAsync(string content, CancellationToken cancellationToken = default)
    {
        if (IsAdministrator() || _elevation is null)
        {
            return await Task.Run(() => SaveAsAdministrator(content), cancellationToken).ConfigureAwait(false);
        }

        var result = await _elevation.RunElevatedAsync(
            ElevatedOperationIds.HostsSave,
            new HostsSavePayload(content),
            "diagnostics:hosts",
            "保存 Windows hosts 文件",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new HostsSaveResult(result.Success, result.Message);
    }

    public async Task<HostsSaveResult> RestoreLatestBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = GetBackups().FirstOrDefault();
        if (backup is null)
        {
            return new HostsSaveResult(false, "没有可恢复的 hosts 备份");
        }

        return await RestoreBackupAsync(backup.Path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostsSaveResult> RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return new HostsSaveResult(false, "请选择一个 hosts 备份");
        }

        var fullPath = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(BackupDirectory);
        if (!fullPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".hosts", StringComparison.OrdinalIgnoreCase))
        {
            return new HostsSaveResult(false, "备份路径不在 EdgeKit hosts 备份目录内");
        }

        if (!File.Exists(fullPath))
        {
            return new HostsSaveResult(false, "hosts 备份不存在");
        }

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        var result = await SaveAsync(content, cancellationToken).ConfigureAwait(false);
        return result.Success
            ? result with { Message = "已恢复备份 " + Path.GetFileName(fullPath) }
            : result;
    }

    public ToolActionResult FlushDns()
    {
        try
        {
            return NativeMethods.DnsFlushResolverCache()
                ? new ToolActionResult(true, "DNS 缓存已刷新")
                : new ToolActionResult(false, "DNS 缓存刷新失败");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ToolActionResult(false, "DNS 刷新不可用：" + ex.Message);
        }
    }

    public string BuildHostsReport(HostsFileSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit Hosts 检查");
        builder.AppendLine($"路径: {snapshot.Path}");
        builder.AppendLine($"刷新时间: {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"管理员权限: {(snapshot.IsAdministrator ? "是" : "否")}");
        builder.AppendLine($"记录行: {snapshot.Rows.Count(r => r.IsEntry)}");
        builder.AppendLine($"问题: {snapshot.Issues.Count}");
        foreach (var issue in snapshot.Issues)
        {
            builder.AppendLine($"  第 {issue.LineNumber} 行 · {issue.Title}: {issue.Detail}");
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
                WriteResult(fullPath, "请求路径不在 EdgeKit pending-hosts 目录内");
                return 2;
            }

            if (!File.Exists(fullPath))
            {
                WriteResult(fullPath, "保存请求不存在");
                return 3;
            }

            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            var result = SaveAsAdministrator(content);
            if (!result.Success)
            {
                WriteResult(fullPath, result.Message);
                return 1;
            }

            WriteResult(fullPath, "hosts 已保存");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            try
            {
                WriteResult(requestPath, ex.Message);
            }
            catch
            {
                // Best effort only; the parent process still receives the exit code.
            }

            return 1;
        }
    }

    public static HostsSaveResult SaveAsAdministrator(string content)
    {
        EnsureAppDirectories();

        try
        {
            BackupCurrentHosts();
            File.WriteAllText(HostsPath, content, HostsEncoding);
            return new HostsSaveResult(true, "hosts 已保存");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new HostsSaveResult(false, "保存失败：" + ex.Message);
        }
    }

    private static HostsLineInfo ParseLine(int lineNumber, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return new HostsLineInfo(lineNumber, raw, false, false, true, string.Empty, Array.Empty<string>(), string.Empty);
        }

        var enabled = !trimmed.StartsWith('#');
        var probe = enabled ? raw : RemoveLeadingComment(raw);
        var comment = string.Empty;
        var commentIndex = probe.IndexOf('#');
        if (commentIndex >= 0)
        {
            comment = probe[(commentIndex + 1)..].Trim();
            probe = probe[..commentIndex];
        }

        var tokens = probe
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return new HostsLineInfo(lineNumber, raw, false, false, true, string.Empty, Array.Empty<string>(), comment);
        }

        if (!IPAddress.TryParse(tokens[0], out _))
        {
            return new HostsLineInfo(lineNumber, raw, false, enabled, false, tokens[0], Array.Empty<string>(), comment);
        }

        if (tokens.Length < 2)
        {
            return new HostsLineInfo(lineNumber, raw, false, enabled, false, tokens[0], Array.Empty<string>(), comment);
        }

        return new HostsLineInfo(lineNumber, raw, true, enabled, false, tokens[0], tokens.Skip(1).ToArray(), comment);
    }

    private static string RemoveLeadingComment(string line)
    {
        var index = line.IndexOf('#');
        if (index < 0)
        {
            return line;
        }

        var before = line[..index];
        var after = line[(index + 1)..];
        if (after.StartsWith(' '))
        {
            after = after[1..];
        }

        return before + after;
    }

    private static HostsBackupInfo[] GetBackups()
    {
        EnsureAppDirectories();
        return Directory.EnumerateFiles(BackupDirectory, "*.hosts")
            .Select(path => new HostsBackupInfo(
                path,
                Path.GetFileName(path),
                File.GetLastWriteTime(path)))
            .OrderByDescending(b => b.CreatedAt)
            .Take(20)
            .ToArray();
    }

    private static void BackupCurrentHosts()
    {
        EnsureAppDirectories();
        if (!File.Exists(HostsPath))
        {
            return;
        }

        var path = Path.Combine(BackupDirectory, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.hosts");
        File.Copy(HostsPath, path, overwrite: false);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void EnsureAppDirectories()
    {
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(PendingDirectory);
    }

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static void WriteResult(string requestPath, string message)
        => File.WriteAllText(GetResultPath(requestPath), message, Encoding.UTF8);

    private static string GetResultPath(string requestPath)
        => requestPath + ".result";

    private static string HostsPath
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDirectory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? "Sysnative"
                : "System32";
            return Path.Combine(windows, systemDirectory, "drivers", "etc", "hosts");
        }
    }

    private static string EdgeKitDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeKit");

    private static string BackupDirectory
        => Path.Combine(EdgeKitDirectory, "hosts-backups");

    private static string PendingDirectory
        => Path.Combine(EdgeKitDirectory, "pending-hosts");
}

public sealed record HostsFileSnapshot(
    string Path,
    DateTime RefreshedAt,
    bool IsAdministrator,
    string Content,
    IReadOnlyList<HostsLineInfo> Rows,
    IReadOnlyList<HostsIssue> Issues,
    IReadOnlyList<HostsBackupInfo> Backups)
{
    public string SummaryText
        => $"{Rows.Count(r => r.IsEntry)} 条记录 · {Issues.Count} 个问题 · {(IsAdministrator ? "管理员" : "普通权限")} · {RefreshedAt:HH:mm:ss}";
}

public sealed record HostsLineInfo(
    int LineNumber,
    string RawText,
    bool IsEntry,
    bool IsEnabled,
    bool IsIgnorable,
    string Address,
    IReadOnlyList<string> HostnamesList,
    string Comment)
{
    public string LineNumberText => LineNumber.ToString(CultureInfo.InvariantCulture);

    public string Hostnames => HostnamesList.Count == 0 ? "无域名" : string.Join(", ", HostnamesList);

    public string StatusText
        => IsIgnorable
            ? "空行/注释"
            : IsEntry
                ? (IsEnabled ? "启用" : "禁用")
                : "无法识别";

    public string Summary => IsEntry ? $"{Address} -> {Hostnames}" : RawText.Trim();

    public bool CanToggle => IsEntry;
}

public sealed record HostsIssue(int LineNumber, string Title, string Detail)
{
    public string DisplayText => $"第 {LineNumber} 行 · {Title}: {Detail}";
}

public sealed record HostsBackupInfo(string Path, string Name, DateTime CreatedAt)
{
    public string DisplayText => $"{Name} · {CreatedAt:yyyy-MM-dd HH:mm:ss}";
}

public sealed record HostsSaveResult(bool Success, string Message);
