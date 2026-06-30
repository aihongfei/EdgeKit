using System.Text.Json;
using EdgeKit.Core.Agent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace EdgeKit.Services.Agent;

public sealed class McpToolService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AIFunction>> BuildToolsAsync(
        AgentSettings settings,
        Func<string, AIFunction, JsonElement, CancellationToken, Task<string>> invoker,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableMcpTools || string.IsNullOrWhiteSpace(settings.McpServersJson))
        {
            return Array.Empty<AIFunction>();
        }

        var result = new List<AIFunction>();
        foreach (var server in ParseServers(settings.McpServersJson))
        {
            var tools = await ListServerToolsAsync(server, cancellationToken).ConfigureAwait(false);
            foreach (var tool in tools)
            {
                var edgeToolId = BuildToolId(server.Name, tool.Name);
                result.Add(new AgentRuntimeFunction(
                    edgeToolId,
                    "[MCP:" + server.Name + "] " + tool.Description,
                    tool.JsonSchema,
                    JsonOptions,
                    (arguments, token) => invoker(edgeToolId, tool, ToJsonElement(arguments), token),
                    tool.ReturnJsonSchema));
            }
        }

        return result;
    }

    public AgentToolDescriptor? FindDescriptor(string toolId, AgentSettings settings)
    {
        if (!toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
            || !settings.EnableMcpTools)
        {
            return null;
        }

        return new AgentToolDescriptor(
            toolId,
            "MCP 工具",
            "调用本地 MCP server 提供的外部工具。",
            AgentToolRisk.UserWrite,
            new[] { AgentConversationMode.Chat, AgentConversationMode.WindowsConfig },
            true);
    }

    public async Task<string> InvokeAsync(
        string toolId,
        JsonElement arguments,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        var (serverName, toolName) = ParseToolId(toolId);
        var server = ParseServers(settings.McpServersJson)
            .FirstOrDefault(s => SanitizeName(s.Name).Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            throw new InvalidOperationException("未找到 MCP server: " + serverName);
        }

        var tools = await ListServerToolsAsync(server, cancellationToken).ConfigureAwait(false);
        var tool = tools.FirstOrDefault(t => SanitizeName(t.Name).Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            throw new InvalidOperationException("未找到 MCP 工具: " + toolName);
        }

        var values = ToArguments(arguments);
        var result = await tool.CallAsync(values, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<IReadOnlyList<McpClientTool>> ListServerToolsAsync(
        McpServerConfig server,
        CancellationToken cancellationToken)
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Arguments.ToList(),
                WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                InheritEnvironmentVariables = true,
                EnvironmentVariables = server.EnvironmentVariables.Count == 0
                    ? null
                    : server.EnvironmentVariables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).ToArray();
    }

    private static IReadOnlyList<McpServerConfig> ParseServers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("mcpServers", out var servers)
            || servers.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("MCP 配置必须是通用格式：包含 mcpServers 对象。");
        }

        var result = new List<McpServerConfig>();
        foreach (var server in servers.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                throw new JsonException("MCP server 名称不能为空。");
            }

            var item = server.Value;
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("MCP server 配置必须是对象: " + server.Name);
            }

            var type = GetOptionalString(item, "type");
            if (!string.IsNullOrWhiteSpace(type) && !type.Equals("stdio", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("当前仅支持 stdio MCP server: " + server.Name);
            }

            result.Add(new McpServerConfig(
                server.Name.Trim(),
                GetString(item, "command"),
                GetStringArray(item, "args"),
                GetStringMap(item, "env"),
                GetOptionalString(item, "cwd")));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> ToArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>();
        }

        return arguments.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal);
    }

    private static JsonElement ToJsonElement(AIFunctionArguments arguments)
        => JsonSerializer.SerializeToElement(
            arguments.ToDictionary(pair => pair.Key, pair => NormalizeArgumentValue(pair.Value), StringComparer.Ordinal),
            JsonOptions);

    private static object? NormalizeArgumentValue(object? value)
        => value switch
        {
            JsonElement element => element.ValueKind == JsonValueKind.Undefined ? null : element.Clone(),
            _ => value
        };

    private static object? ToObject(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value), StringComparer.Ordinal),
            _ => null
        };

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.ToString()))
        {
            throw new JsonException("MCP server 缺少字段: " + name);
        }

        return value.ToString().Trim();
    }

    private static string GetOptionalString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ToString().Trim();
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("MCP server 字段必须是数组: " + name);
        }

        return value.EnumerateArray()
            .Select(v => v.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string?> GetStringMap(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return new Dictionary<string, string?>();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("MCP server 字段必须是对象: " + name);
        }

        return value.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString(),
                StringComparer.Ordinal);
    }

    private static string BuildToolId(string serverName, string toolName)
        => "mcp_" + SanitizeName(serverName) + "__" + SanitizeName(toolName);

    private static (string ServerName, string ToolName) ParseToolId(string toolId)
    {
        var value = toolId.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) ? toolId[4..] : toolId;
        var parts = value.Split("__", 2, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            throw new ArgumentException("MCP 工具 ID 无效: " + toolId);
        }

        return (parts[0], parts[1]);
    }

    private static string SanitizeName(string value)
        => string.Concat(value.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_'));

    private sealed record McpServerConfig(
        string Name,
        string Command,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string?> EnvironmentVariables,
        string WorkingDirectory);
}
