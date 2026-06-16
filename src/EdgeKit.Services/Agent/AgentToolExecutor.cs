using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdgeKit.Core.Agent;
using EdgeKit.Core.Clipboard;
using EdgeKit.Core.Services;
using EdgeKit.Services.Diagnostics;
using EdgeKit.Services.Settings;
using EdgeKit.Services.SystemOperations;
using EdgeKit.Services.Text;

namespace EdgeKit.Services.Agent;

public sealed class AgentToolExecutor
{
    private const int MaxFileSearchBytes = 512 * 1024;
    private const int MinSearchScannedFiles = 2_000;
    private const int MaxSearchScannedFiles = 10_000;
    private const int MaxSearchDirectories = 1_500;
    private const int MaxListScannedEntries = 5_000;
    private static readonly TimeSpan MaxSearchDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxListDuration = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AgentToolRegistry _registry;
    private readonly SystemDiagnosticsService _diagnostics;
    private readonly HostsFileService _hosts;
    private readonly EnvironmentVariableService _environment;
    private readonly FileLockService _fileLocks;
    private readonly TextProcessingService _text;
    private readonly IClipboardRepository _clipboard;
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly McpToolService _mcpTools;
    private readonly ElevatedOperationService _elevation;

    public AgentToolExecutor(
        AgentToolRegistry registry,
        SystemDiagnosticsService diagnostics,
        HostsFileService hosts,
        EnvironmentVariableService environment,
        FileLockService fileLocks,
        TextProcessingService text,
        IClipboardRepository clipboard,
        HttpClient http,
        ISettingsService settings,
        McpToolService mcpTools,
        ElevatedOperationService elevation)
    {
        _registry = registry;
        _diagnostics = diagnostics;
        _hosts = hosts;
        _environment = environment;
        _fileLocks = fileLocks;
        _text = text;
        _clipboard = clipboard;
        _http = http;
        _settings = settings;
        _mcpTools = mcpTools;
        _elevation = elevation;
    }

    public AgentToolDescriptor? Find(string toolId) => _registry.Find(toolId);

    public bool CanAutoExecute(AgentToolDescriptor descriptor, AgentSettings settings)
    {
        if (settings.ActionMode == AgentActionMode.SuggestOnly)
        {
            return false;
        }

        if (descriptor.Risk == AgentToolRisk.ReadOnly && !descriptor.RequiresApproval)
        {
            return true;
        }

        return false;
    }

    public bool ShouldExposeTool(AgentToolDescriptor descriptor, AgentSettings settings)
        => settings.ActionMode != AgentActionMode.SuggestOnly || descriptor.Risk == AgentToolRisk.ReadOnly;

    public async Task<AgentToolExecutionResult> ExecuteAsync(
        string toolId,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase))
            {
                var mcpResult = await _mcpTools.InvokeAsync(toolId, arguments, GetSettingsForToolExecution(), cancellationToken).ConfigureAwait(false);
                return new AgentToolExecutionResult(true, TrimResult(mcpResult), string.Empty);
            }

            var result = toolId switch
            {
                "system_summary" => _diagnostics.BuildSystemReport(_diagnostics.GetSystemSnapshot()),
                "network_summary" => await BuildNetworkSummaryAsync(cancellationToken).ConfigureAwait(false),
                "ports_list" => _diagnostics.BuildPortReport(_diagnostics.GetPortEntries()),
                "ping_host" => "Ping" + Environment.NewLine + (await _diagnostics.PingAsync(GetString(arguments, "host"), cancellationToken).ConfigureAwait(false)).DisplayText,
                "resolve_dns" => (await _diagnostics.ResolveDnsAsync(GetString(arguments, "host"), cancellationToken).ConfigureAwait(false)).ToReportText(),
                "probe_tcp" => (await _diagnostics.ProbeTcpAsync(GetString(arguments, "host"), GetInt(arguments, "port"), cancellationToken).ConfigureAwait(false)).ToReportText(),
                "hosts_report" => _hosts.BuildHostsReport(_hosts.ReadSnapshot()),
                "env_report" => _environment.BuildReport(_environment.ReadSnapshot().Entries),
                "analyze_path" => BuildPathAnalysis(arguments),
                "file_lock_scan" => _fileLocks.BuildReport(await _fileLocks.ScanAsync(GetString(arguments, "path"), cancellationToken).ConfigureAwait(false)),
                "text_json_format" => FormatTextResult(_text.FormatJson(GetString(arguments, "text"))),
                "text_base64_decode" => FormatTextResult(_text.Base64Decode(GetString(arguments, "text"))),
                "text_url_decode" => _text.UrlDecode(GetString(arguments, "text")),
                "clipboard_search" => BuildClipboardSearch(arguments),
                "file_read" => await ReadFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "file_list" => ListFiles(arguments),
                "file_search" => await SearchFilesAsync(arguments, cancellationToken).ConfigureAwait(false),
                "file_write" => await WriteFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "file_patch" => await PatchFileAsync(arguments, cancellationToken).ConfigureAwait(false),
                "file_delete_recycle" => FormatActionResult(await DeleteFileToRecycleAsync(arguments, cancellationToken).ConfigureAwait(false)),
                "shell_run" => await RunShellAsync(arguments, cancellationToken).ConfigureAwait(false),
                "web_search" => await SearchWebAsync(arguments, cancellationToken).ConfigureAwait(false),
                "web_fetch" => await FetchWebAsync(arguments, cancellationToken).ConfigureAwait(false),
                "open_windows_settings" => OpenWindowsSettings(arguments),
                "flush_dns" => FormatActionResult(_hosts.FlushDns()),
                "save_hosts" => FormatHostsSaveResult(await _hosts.SaveAsync(GetString(arguments, "content"), cancellationToken).ConfigureAwait(false)),
                "set_env" => FormatActionResult(await _environment.SaveAsync(GetTarget(arguments), GetString(arguments, "name"), GetString(arguments, "value"), cancellationToken).ConfigureAwait(false)),
                "delete_env" => FormatActionResult(await _environment.DeleteAsync(GetTarget(arguments), GetString(arguments, "name"), cancellationToken).ConfigureAwait(false)),
                "file_lock_delete_recycle" => FormatActionResult(await ExecuteRecycleDeleteAsync(arguments, cancellationToken).ConfigureAwait(false)),
                "file_lock_kill_delete" => FormatActionResult(await ExecuteKillDeleteAsync(arguments, cancellationToken).ConfigureAwait(false)),
                "kill_port_owner" => await KillPortOwnerAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => "未知工具: " + toolId
            };

            return new AgentToolExecutionResult(true, TrimResult(result), string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException or NotSupportedException or TimeoutException)
        {
            return new AgentToolExecutionResult(false, string.Empty, ex.Message);
        }
    }

    public bool IsTrustedToolCall(AgentToolDescriptor descriptor, JsonElement arguments, AgentSettings settings)
    {
        if (settings.ActionMode != AgentActionMode.AutoWithWhitelist)
        {
            return false;
        }

        try
        {
            return descriptor.Id switch
            {
                "file_write" or "file_patch" => IsTrustedPath(GetString(arguments, "path"), settings.TrustedDirectories),
                "shell_run" => !(GetOptionalBool(arguments, "runAsAdministrator") ?? false)
                    && IsWhitelistedCommand(GetString(arguments, "command"), settings.ShellCommandWhitelist),
                _ => false
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string ArgumentsSummary(JsonElement arguments)
    {
        var json = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        return json.Length <= 500 ? json : json[..500] + "...";
    }

    public static JsonElement ParseArguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return document.RootElement.Clone();
    }

    private async Task<string> BuildNetworkSummaryAsync(CancellationToken cancellationToken)
    {
        var publicIp = string.Empty;
        var publicIpStatus = "未查询";
        try
        {
            publicIp = await _diagnostics.GetPublicIpAsync(cancellationToken).ConfigureAwait(false);
            publicIpStatus = string.IsNullOrWhiteSpace(publicIp) ? "未查询到公网 IP" : "查询成功";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            publicIpStatus = ex.Message;
        }

        return _diagnostics.BuildNetworkReport(
            _diagnostics.GetNetworkSnapshot(publicIp, publicIpStatus),
            _diagnostics.GetProxySnapshot());
    }

    private string BuildPathAnalysis(JsonElement arguments)
    {
        var value = GetOptionalString(arguments, "value");
        if (string.IsNullOrWhiteSpace(value))
        {
            var target = GetOptionalString(arguments, "target");
            var snapshot = _environment.ReadSnapshot();
            var entry = snapshot.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, "Path", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Target.ToString(), target, StringComparison.OrdinalIgnoreCase));
            value = entry?.Value ?? string.Empty;
        }

        var items = _environment.AnalyzePath(value);
        return string.Join(Environment.NewLine, items.Select(i => $"{i.Index}. {i.DisplayValue} - {i.IssueText}"));
    }

    private string BuildClipboardSearch(JsonElement arguments)
    {
        var query = GetOptionalString(arguments, "query");
        var limit = Math.Clamp(GetOptionalInt(arguments, "limit") ?? 10, 1, 20);
        var items = _clipboard.Get(null, null, query, limit);
        if (items.Count == 0)
        {
            return "未找到剪贴板历史。";
        }

        return string.Join(Environment.NewLine, items.Select(i =>
            $"{i.Id}. [{i.Kind}] {i.Preview} · {i.CreatedUtc:yyyy-MM-dd HH:mm:ss}"));
    }

    private async Task<string> ReadFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = NormalizeExistingFile(GetString(arguments, "path"));
        var maxBytes = Math.Clamp(GetOptionalInt(arguments, "maxBytes") ?? 64 * 1024, 1, 256 * 1024);
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
        {
            return $"文件过大，仅允许读取 {maxBytes} 字节以内。当前大小: {info.Length} 字节。";
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return $"路径: {path}{Environment.NewLine}大小: {info.Length} 字节{Environment.NewLine}{Environment.NewLine}{text}";
    }

    private string ListFiles(JsonElement arguments)
    {
        var path = NormalizeExistingDirectory(GetString(arguments, "path"));
        var pattern = GetOptionalString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "*";
        }

        var recursive = GetOptionalBool(arguments, "recursive") ?? false;
        var limit = Math.Clamp(GetOptionalInt(arguments, "limit") ?? 100, 1, 500);
        var entries = new List<string>();
        var stats = new FileListStats(MaxListScannedEntries, MaxSearchDirectories, DateTime.UtcNow + MaxListDuration);

        foreach (var entry in EnumerateEntriesSafely(path, pattern, recursive, stats))
        {
            if (entries.Count >= limit || stats.IsBudgetExhausted)
            {
                break;
            }

            try
            {
                var kind = Directory.Exists(entry) ? "dir" : "file";
                entries.Add($"{kind} {entry}");
            }
            catch (Exception ex) when (IsSkippableFileSystemException(ex))
            {
                stats.SkippedInaccessible++;
            }
        }

        var suffixLines = new List<string>();
        if (stats.SkippedInaccessible > 0)
        {
            suffixLines.Add($"已跳过不可访问项目: {stats.SkippedInaccessible}");
        }

        if (stats.SkippedReparsePoints > 0)
        {
            suffixLines.Add($"已跳过系统链接目录: {stats.SkippedReparsePoints}");
        }

        if (stats.IsBudgetExhausted)
        {
            suffixLines.Add($"已达到列表扫描预算，结果可能不完整。已扫描项目: {stats.EntriesScanned}");
        }

        var suffix = suffixLines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, suffixLines) : string.Empty;
        var text = entries.Count == 0
            ? "未找到文件或文件夹。"
            : string.Join(Environment.NewLine, entries);
        return text + suffix;
    }

    private async Task<string> SearchFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = NormalizeExistingDirectory(GetString(arguments, "path"));
        var query = GetString(arguments, "query");
        var pattern = GetOptionalString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "*";
        }

        var recursive = GetOptionalBool(arguments, "recursive") ?? true;
        var limit = Math.Clamp(GetOptionalInt(arguments, "limit") ?? 50, 1, 200);
        var results = new List<string>();
        var stats = new FileSearchStats(
            Math.Clamp(limit * 500, MinSearchScannedFiles, MaxSearchScannedFiles),
            MaxSearchDirectories,
            DateTime.UtcNow + MaxSearchDuration);

        foreach (var file in EnumerateFilesSafely(path, pattern, recursive, stats, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= limit)
            {
                break;
            }

            var fileName = Path.GetFileName(file);
            if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add("name " + file);
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    stats.SkippedReparsePoints++;
                    continue;
                }
            }
            catch (Exception ex) when (IsSkippableFileSystemException(ex))
            {
                stats.SkippedInaccessible++;
                continue;
            }

            if (info.Length > MaxFileSearchBytes)
            {
                stats.SkippedLarge++;
                continue;
            }

            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var start = Math.Max(0, index - 80);
                    var length = Math.Min(text.Length - start, query.Length + 160);
                    var snippet = text.Substring(start, length).ReplaceLineEndings(" ");
                    results.Add($"content {file}: {snippet}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                stats.SkippedUnreadable++;
            }
        }

        var summary = BuildSearchStatsSummary(stats);
        return results.Count == 0
            ? "未找到匹配内容。" + summary
            : string.Join(Environment.NewLine, results) + summary;
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        string pattern,
        bool recursive,
        FileSearchStats stats,
        CancellationToken cancellationToken)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stats.IsBudgetExhausted)
            {
                yield break;
            }

            var directory = directories.Pop();
            stats.DirectoriesVisited++;

            foreach (var file in EnumerateDirectoryFilesSafely(directory, pattern, stats))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stats.IsBudgetExhausted)
                {
                    yield break;
                }

                stats.FilesScanned++;
                yield return file;
            }

            if (!recursive)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectoriesSafely(directory, stats))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stats.IsBudgetExhausted)
                {
                    yield break;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        stats.SkippedReparsePoints++;
                        continue;
                    }
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex))
                {
                    stats.SkippedInaccessible++;
                    continue;
                }

                directories.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumerateEntriesSafely(
        string root,
        string pattern,
        bool recursive,
        FileListStats stats)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            if (stats.IsBudgetExhausted)
            {
                yield break;
            }

            var directory = directories.Pop();
            stats.DirectoriesVisited++;

            foreach (var entry in EnumerateDirectoryEntriesSafely(directory, pattern, stats))
            {
                if (stats.IsBudgetExhausted)
                {
                    yield break;
                }

                stats.EntriesScanned++;
                yield return entry;
            }

            if (!recursive)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectoriesSafely(directory, stats))
            {
                if (stats.IsBudgetExhausted)
                {
                    yield break;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        stats.SkippedReparsePoints++;
                        continue;
                    }
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex))
                {
                    stats.SkippedInaccessible++;
                    continue;
                }

                directories.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoryEntriesSafely(string directory, string pattern, FileListStats stats)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directory, pattern, CreateEnumerationOptions(recursive: false)).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex))
                {
                    stats.SkippedInaccessible++;
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateDirectoryFilesSafely(string directory, string pattern, FileSearchStats stats)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(directory, pattern, CreateEnumerationOptions(recursive: false)).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex))
                {
                    stats.SkippedInaccessible++;
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateChildDirectoriesSafely(string directory, IFileEnumerationStats stats)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(directory, "*", CreateEnumerationOptions(recursive: false)).GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex))
                {
                    stats.SkippedInaccessible++;
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static EnumerationOptions CreateEnumerationOptions(bool recursive)
        => new()
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

    private static bool IsSkippableFileSystemException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or PathTooLongException
            or NotSupportedException;

    private static string BuildSearchStatsSummary(FileSearchStats stats)
    {
        var skipped = stats.SkippedInaccessible + stats.SkippedUnreadable + stats.SkippedLarge + stats.SkippedReparsePoints;
        var lines = new List<string>
        {
            string.Empty,
            $"扫描文件: {stats.FilesScanned}, 扫描目录: {stats.DirectoriesVisited}"
        };

        if (skipped > 0)
        {
            lines.Add($"已跳过: {skipped} (不可访问 {stats.SkippedInaccessible}, 不可读 {stats.SkippedUnreadable}, 过大 {stats.SkippedLarge}, 系统链接 {stats.SkippedReparsePoints})");
        }

        if (stats.IsBudgetExhausted)
        {
            lines.Add("已达到搜索预算，结果可能不完整。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> WriteFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = NormalizeWritablePath(GetString(arguments, "path"));
        var content = GetOptionalString(arguments, "content");
        var append = GetOptionalBool(arguments, "append") ?? false;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (append)
            {
                await File.AppendAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
                return "已追加写入文件: " + path;
            }

            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            return "已写入文件: " + path;
        }
        catch (Exception ex) when (ElevatedOperationService.IsAccessDenied(ex) && !_elevation.IsAdministrator)
        {
            var result = await _elevation.RunElevatedAsync(
                ElevatedOperationIds.FileWrite,
                new FileWritePayload(path, content, append),
                "agent:file_write",
                (append ? "追加写入文件 " : "写入文件 ") + path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result.Message;
        }
    }

    private async Task<string> PatchFileAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = NormalizeExistingFile(GetString(arguments, "path"));
        var oldText = GetString(arguments, "oldText");
        var newText = GetOptionalString(arguments, "newText");

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var index = text.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ArgumentException("未找到 oldText，未修改文件。");
            }

            if (text.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException("oldText 出现多次，请提供更精确的文本。");
            }

            var updated = text.Replace(oldText, newText, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, updated, cancellationToken).ConfigureAwait(false);
            return "已替换文件文本: " + path;
        }
        catch (Exception ex) when (ElevatedOperationService.IsAccessDenied(ex) && !_elevation.IsAdministrator)
        {
            var result = await _elevation.RunElevatedAsync(
                ElevatedOperationIds.FilePatch,
                new FilePatchPayload(path, oldText, newText),
                "agent:file_patch",
                "替换文件文本 " + path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result.Message;
        }
    }

    private async Task<ToolActionResult> DeleteFileToRecycleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = NormalizeWritablePath(GetString(arguments, "path"));
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new ArgumentException("路径不存在: " + path);
        }

        var result = await _fileLocks.DeleteToRecycleBinAsync(path, isDirectory, cancellationToken).ConfigureAwait(false);
        if (result.Success || _elevation.IsAdministrator || !IsLikelyAccessDeniedMessage(result.Message))
        {
            return result;
        }

        var elevated = await _elevation.RunElevatedAsync(
            ElevatedOperationIds.FileDeleteRecycle,
            new FileDeleteRecyclePayload(path, isDirectory),
            "agent:file_delete_recycle",
            "删除到回收站 " + path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return elevated.ToToolActionResult();
    }

    private async Task<string> RunShellAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var command = GetString(arguments, "command");
        var workingDirectory = GetOptionalString(arguments, "workingDirectory");
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = NormalizeExistingDirectory(workingDirectory);
        }

        var timeoutSeconds = Math.Clamp(GetOptionalInt(arguments, "timeoutSeconds") ?? 30, 1, 120);
        var runAsAdministrator = GetOptionalBool(arguments, "runAsAdministrator") ?? false;
        if (runAsAdministrator)
        {
            var result = await _elevation.RunElevatedAsync(
                ElevatedOperationIds.PowerShellRun,
                new PowerShellRunPayload(command, workingDirectory ?? string.Empty, timeoutSeconds),
                "agent:shell_run",
                "管理员 PowerShell: " + command,
                timeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var elevatedSummary = FormatShellResult(result.ExitCode ?? -1, result.Stdout, result.Stderr);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message + Environment.NewLine + elevatedSummary);
            }

            return result.Message + Environment.NewLine + elevatedSummary;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + EncodePowerShellCommand(command),
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workingDirectory,
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
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("Shell 命令执行超时。");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var summary = FormatShellResult(process.ExitCode, output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(summary);
        }

        return summary;
    }

    private async Task<string> SearchWebAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        var limit = Math.Clamp(GetOptionalInt(arguments, "limit") ?? 5, 1, 10);
        var settings = GetSettingsForToolExecution();
        if (string.IsNullOrWhiteSpace(settings.SearchApiKey))
        {
            throw new InvalidOperationException("未配置 Web Search API Key。");
        }

        return settings.SearchProvider == AgentSearchProvider.Tavily
            ? await SearchTavilyAsync(query, limit, settings.SearchApiKey, cancellationToken).ConfigureAwait(false)
            : await SearchBraveAsync(query, limit, settings.SearchApiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> FetchWebAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var url = GetString(arguments, "url");
        var maxBytes = Math.Clamp(GetOptionalInt(arguments, "maxBytes") ?? 128 * 1024, 1, 512 * 1024);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("只允许读取 http/https URL。");
        }

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        var text = Encoding.UTF8.GetString(memory.ToArray());
        return $"URL: {uri}{Environment.NewLine}{StripMarkup(text)}";
    }

    private static string OpenWindowsSettings(JsonElement arguments)
    {
        var uri = GetString(arguments, "uri");
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只允许打开 ms-settings: Windows 设置 URI");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true
        });
        return "已打开 Windows 设置: " + uri;
    }

    private async Task<ToolActionResult> ExecuteKillDeleteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = GetString(arguments, "path");
        var isDirectory = GetOptionalBool(arguments, "isDirectory") ?? Directory.Exists(path);
        var processIds = GetIntArray(arguments, "processIds").ToHashSet();
        var snapshot = await _fileLocks.ScanAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = processIds.Count == 0
            ? snapshot.Entries
            : snapshot.Entries.Where(e => processIds.Contains(e.ProcessId)).ToArray();
        return await _fileLocks.KillProcessesAndDeleteAsync(path, isDirectory, entries, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolActionResult> ExecuteRecycleDeleteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = GetString(arguments, "path");
        var isDirectory = GetOptionalBool(arguments, "isDirectory") ?? Directory.Exists(path);
        return await _fileLocks.DeleteToRecycleBinAsync(path, isDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> KillPortOwnerAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var processId = GetInt(arguments, "processId");
        var port = GetOptionalInt(arguments, "port") ?? 0;
        var entries = _diagnostics.GetPortEntries();
        var entry = entries.FirstOrDefault(e => e.ProcessId == processId && (port <= 0 || e.Port == port));
        if (entry is null)
        {
            throw new ArgumentException("没有找到匹配的端口占用进程");
        }

        var result = _diagnostics.KillPortOwner(entry);
        if (result.Success || _elevation.IsAdministrator || !IsLikelyAccessDeniedMessage(result.Message))
        {
            return result.Success ? result.Message : "失败: " + result.Message;
        }

        var elevated = await _elevation.RunElevatedAsync(
            ElevatedOperationIds.ProcessKill,
            new ProcessKillPayload(entry.ProcessId, entry.ProcessName),
            "agent:kill_port_owner",
            $"结束端口占用进程 PID {entry.ProcessId}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return elevated.Success ? elevated.Message : "失败: " + elevated.Message;
    }

    private async Task<string> SearchBraveAsync(string query, int limit, string apiKey, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=" + limit.ToString(CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Subscription-Token", apiKey);
        request.Headers.UserAgent.ParseAdd("EdgeKit/1.0");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("web", out var web)
            || !web.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return "未找到搜索结果。";
        }

        return FormatSearchResults(results.EnumerateArray().Take(limit).Select(item => new WebSearchItem(
            GetPropertyString(item, "title"),
            GetPropertyString(item, "url"),
            GetPropertyString(item, "description"))));
    }

    private async Task<string> SearchTavilyAsync(string query, int limit, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            api_key = apiKey,
            query,
            max_results = limit,
            search_depth = "basic"
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return "未找到搜索结果。";
        }

        return FormatSearchResults(results.EnumerateArray().Take(limit).Select(item => new WebSearchItem(
            GetPropertyString(item, "title"),
            GetPropertyString(item, "url"),
            GetPropertyString(item, "content"))));
    }

    private AgentSettings GetSettingsForToolExecution()
    {
        var searchKey = string.Empty;
        try
        {
            searchKey = SecretProtector.Unprotect(_settings.AiSearchApiKeyEncrypted);
        }
        catch
        {
            searchKey = string.Empty;
        }

        return new AgentSettings(
            _settings.AiEnabled,
            _settings.AiBaseUrl,
            _settings.AiModel,
            string.Empty,
            _settings.AiApiKeyPreview,
            _settings.AiTemperature,
            _settings.AiDefaultMode,
            _settings.AiActionMode,
            _settings.AiAllowClipboardTools,
            _settings.AiEnableFileTools,
            _settings.AiEnableShellTools,
            _settings.AiEnableWebTools,
            _settings.AiEnableMcpTools,
            _settings.AiSearchProvider,
            searchKey,
            _settings.AiSearchApiKeyPreview,
            _settings.AiTrustedDirectories,
            _settings.AiShellCommandWhitelist,
            _settings.AiMcpServersJson,
            _settings.AiContextWindowTokens);
    }

    private static string FormatSearchResults(IEnumerable<WebSearchItem> items)
    {
        var lines = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .Select((i, index) =>
                $"{index + 1}. {EmptyFallback(i.Title)}{Environment.NewLine}{i.Url}{Environment.NewLine}{EmptyFallback(i.Snippet)}")
            .ToArray();
        return lines.Length == 0 ? "未找到搜索结果。" : string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string NormalizeExistingFile(string path)
    {
        var fullPath = NormalizeWritablePath(path);
        if (!File.Exists(fullPath))
        {
            throw new ArgumentException("文件不存在: " + fullPath);
        }

        return fullPath;
    }

    private static string NormalizeExistingDirectory(string path)
    {
        var fullPath = NormalizeWritablePath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException("目录不存在: " + fullPath);
        }

        return fullPath;
    }

    private static string NormalizeWritablePath(string path)
        => Path.GetFullPath(ExpandUserPath(path));

    private static string ExpandUserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。");
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded == "~")
        {
            return userProfile;
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(userProfile, expanded[2..]);
        }

        var alias = ResolveKnownFolderAlias(expanded);
        return alias ?? expanded;
    }

    private static string? ResolveKnownFolderAlias(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || Path.IsPathRooted(normalized))
        {
            return null;
        }

        var root = parts[0];
        var knownFolder = root switch
        {
            "桌面" or "desktop" or "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "文档" or "我的文档" or "documents" or "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "下载" or "downloads" or "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "图片" or "pictures" or "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "音乐" or "music" or "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "视频" or "videos" or "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "用户目录" or "home" or "Home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(knownFolder))
        {
            return null;
        }

        return parts.Length == 1 ? knownFolder : Path.Combine(knownFolder, parts[1]);
    }

    private static bool IsTrustedPath(string path, string trustedDirectories)
    {
        var fullPath = NormalizeWritablePath(path);
        foreach (var root in SplitLines(trustedDirectories))
        {
            var fullRoot = NormalizeWritablePath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWhitelistedCommand(string command, string whitelist)
    {
        var normalized = NormalizeCommand(command);
        return SplitLines(whitelist)
            .Select(NormalizeCommand)
            .Where(prefix => prefix.Length > 0)
            .Any(prefix => normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCommand(string value)
        => Regex.Replace(value.Trim(), "\\s+", " ");

    private static IReadOnlyList<string> SplitLines(string value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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

    private static string StripMarkup(string value)
    {
        var text = Regex.Replace(value, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string GetPropertyString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : string.Empty;

    private static string EmptyFallback(string value)
        => string.IsNullOrWhiteSpace(value) ? "无" : value.Trim();

    private static string FormatShellResult(int exitCode, string output, string error)
        => "ExitCode: " + exitCode + Environment.NewLine +
            "Output:" + Environment.NewLine +
            EmptyFallback(output) + Environment.NewLine +
            "Error:" + Environment.NewLine +
            EmptyFallback(error);

    private static bool IsLikelyAccessDeniedMessage(string message)
        => message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("访问被拒绝", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase);

    private static string FormatTextResult(TextToolResult result)
        => result.IsSuccess ? result.Output : "失败: " + result.Message;

    private static string FormatActionResult(ToolActionResult result)
        => result.Success ? result.Message : "失败: " + result.Message;

    private static string FormatHostsSaveResult(HostsSaveResult result)
        => result.Success ? result.Message : "失败: " + result.Message;

    private static EnvironmentVariableTarget GetTarget(JsonElement arguments)
    {
        var raw = GetString(arguments, "target");
        return raw.Equals("Machine", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("System", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("系统", StringComparison.OrdinalIgnoreCase)
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;
    }

    private static string GetString(JsonElement arguments, string name)
    {
        var value = GetOptionalString(arguments, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"缺少参数 {name}");
        }

        return value.Trim();
    }

    private static string GetOptionalString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out var property)
            && property.ValueKind != JsonValueKind.Null
            && property.ValueKind != JsonValueKind.Undefined)
        {
            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : property.ToString();
        }

        return string.Empty;
    }

    private static int GetInt(JsonElement arguments, string name)
        => GetOptionalInt(arguments, name) ?? throw new ArgumentException($"缺少参数 {name}");

    private static int? GetOptionalInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            {
                return value;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? GetOptionalBool(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var property))
        {
            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<int> GetIntArray(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0)
                .Where(i => i > 0)
                .Distinct()
                .ToArray();
        }

        return Array.Empty<int>();
    }

    private static string TrimResult(string value)
        => value.Length <= 4000 ? value : value[..4000] + Environment.NewLine + "...";

    private sealed record WebSearchItem(string Title, string Url, string Snippet);

    private interface IFileEnumerationStats
    {
        int SkippedInaccessible { get; set; }

        int SkippedReparsePoints { get; set; }

        bool IsBudgetExhausted { get; }
    }

    private sealed class FileListStats(int maxEntries, int maxDirectories, DateTime deadlineUtc) : IFileEnumerationStats
    {
        public int EntriesScanned { get; set; }

        public int DirectoriesVisited { get; set; }

        public int SkippedInaccessible { get; set; }

        public int SkippedReparsePoints { get; set; }

        public bool IsBudgetExhausted
            => EntriesScanned >= maxEntries
                || DirectoriesVisited >= maxDirectories
                || DateTime.UtcNow >= deadlineUtc;
    }

    private sealed class FileSearchStats(int maxFiles, int maxDirectories, DateTime deadlineUtc) : IFileEnumerationStats
    {
        public int FilesScanned { get; set; }

        public int DirectoriesVisited { get; set; }

        public int SkippedInaccessible { get; set; }

        public int SkippedUnreadable { get; set; }

        public int SkippedLarge { get; set; }

        public int SkippedReparsePoints { get; set; }

        public bool IsBudgetExhausted
            => FilesScanned >= maxFiles
                || DirectoriesVisited >= maxDirectories
                || DateTime.UtcNow >= deadlineUtc;
    }
}

public sealed record AgentToolExecutionResult(bool Success, string Result, string Error);
