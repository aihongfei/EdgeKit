using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EdgeKit.Core.Agent;
using EdgeKit.Core.Clipboard;
using EdgeKit.Services.Diagnostics;
using EdgeKit.Services.Text;

namespace EdgeKit.Services.Agent;

public sealed class AgentToolExecutor
{
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

    public AgentToolExecutor(
        AgentToolRegistry registry,
        SystemDiagnosticsService diagnostics,
        HostsFileService hosts,
        EnvironmentVariableService environment,
        FileLockService fileLocks,
        TextProcessingService text,
        IClipboardRepository clipboard)
    {
        _registry = registry;
        _diagnostics = diagnostics;
        _hosts = hosts;
        _environment = environment;
        _fileLocks = fileLocks;
        _text = text;
        _clipboard = clipboard;
    }

    public AgentToolDescriptor? Find(string toolId) => _registry.Find(toolId);

    public bool CanAutoExecute(AgentToolDescriptor descriptor, AgentSettings settings)
        => descriptor.Risk == AgentToolRisk.ReadOnly
            && !descriptor.RequiresApproval
            && settings.ActionMode != AgentActionMode.SuggestOnly;

    public bool ShouldExposeTool(AgentToolDescriptor descriptor, AgentSettings settings)
        => settings.ActionMode != AgentActionMode.SuggestOnly || descriptor.Risk == AgentToolRisk.ReadOnly;

    public async Task<AgentToolExecutionResult> ExecuteAsync(
        string toolId,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
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
                "open_windows_settings" => OpenWindowsSettings(arguments),
                "flush_dns" => FormatActionResult(_hosts.FlushDns()),
                "save_hosts" => FormatHostsSaveResult(await _hosts.SaveAsync(GetString(arguments, "content"), cancellationToken).ConfigureAwait(false)),
                "set_env" => FormatActionResult(await _environment.SaveAsync(GetTarget(arguments), GetString(arguments, "name"), GetString(arguments, "value"), cancellationToken).ConfigureAwait(false)),
                "delete_env" => FormatActionResult(await _environment.DeleteAsync(GetTarget(arguments), GetString(arguments, "name"), cancellationToken).ConfigureAwait(false)),
                "file_lock_delete_recycle" => FormatActionResult(await ExecuteRecycleDeleteAsync(arguments, cancellationToken).ConfigureAwait(false)),
                "file_lock_kill_delete" => FormatActionResult(await ExecuteKillDeleteAsync(arguments, cancellationToken).ConfigureAwait(false)),
                "kill_port_owner" => FormatKillPortOwner(arguments),
                _ => "未知工具: " + toolId
            };

            return new AgentToolExecutionResult(true, TrimResult(result), string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new AgentToolExecutionResult(false, string.Empty, ex.Message);
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

    private string FormatKillPortOwner(JsonElement arguments)
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
        return result.Success ? result.Message : "失败: " + result.Message;
    }

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
}

public sealed record AgentToolExecutionResult(bool Success, string Result, string Error);
