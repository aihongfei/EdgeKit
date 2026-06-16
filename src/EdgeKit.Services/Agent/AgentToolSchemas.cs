using System.Text.Json;

namespace EdgeKit.Services.Agent;

internal static class AgentToolSchemas
{
    private static readonly Dictionary<string, string[]> RequiredByTool = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping_host"] = ["host"],
        ["resolve_dns"] = ["host"],
        ["probe_tcp"] = ["host", "port"],
        ["file_lock_scan"] = ["path"],
        ["text_json_format"] = ["text"],
        ["text_base64_decode"] = ["text"],
        ["text_url_decode"] = ["text"],
        ["file_read"] = ["path"],
        ["file_list"] = ["path"],
        ["file_search"] = ["path", "query"],
        ["file_write"] = ["path", "content"],
        ["file_patch"] = ["path", "oldText", "newText"],
        ["file_delete_recycle"] = ["path"],
        ["shell_run"] = ["command"],
        ["web_search"] = ["query"],
        ["web_fetch"] = ["url"],
        ["open_windows_settings"] = ["uri"],
        ["save_hosts"] = ["content"],
        ["set_env"] = ["target", "name", "value"],
        ["delete_env"] = ["target", "name"],
        ["file_lock_delete_recycle"] = ["path"],
        ["file_lock_kill_delete"] = ["path", "processIds"],
        ["kill_port_owner"] = ["processId"]
    };

    private static readonly Dictionary<string, ToolProperty[]> PropertiesByTool = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping_host"] = [Text("host", "Hostname or IP address to ping.")],
        ["resolve_dns"] = [Text("host", "Hostname to resolve.")],
        ["probe_tcp"] = [Text("host", "Hostname or IP address."), Integer("port", "TCP port.")],
        ["analyze_path"] = [Text("value", "PATH value to analyze. Optional if target is provided."), Text("target", "Environment target: User or Machine.")],
        ["file_lock_scan"] = [Text("path", "File or directory path to inspect for locks.")],
        ["text_json_format"] = [Text("text", "JSON text to format.")],
        ["text_base64_decode"] = [Text("text", "Base64 text to decode.")],
        ["text_url_decode"] = [Text("text", "URL-encoded text to decode.")],
        ["clipboard_search"] = [Text("query", "Optional clipboard search text."), Integer("limit", "Maximum number of items.")],
        ["file_read"] = [Text("path", PathDescription("text file path to read")), Integer("maxBytes", "Maximum bytes to read.")],
        ["file_list"] = [Text("path", PathDescription("directory path to list")), Text("pattern", "File search pattern, for example *.txt."), Boolean("recursive", "Whether to include subdirectories."), Integer("limit", "Maximum number of entries.")],
        ["file_search"] = [Text("path", PathDescription("directory path to search")), Text("query", "File name or text query."), Text("pattern", "File search pattern, for example *.txt."), Boolean("recursive", "Whether to include subdirectories."), Integer("limit", "Maximum number of matches.")],
        ["file_write"] = [Text("path", PathDescription("target file path")), Text("content", "Text content to write."), Boolean("append", "Append instead of overwrite.")],
        ["file_patch"] = [Text("path", PathDescription("target text file path")), Text("oldText", "Exact text to replace."), Text("newText", "Replacement text.")],
        ["file_delete_recycle"] = [Text("path", PathDescription("file or directory path to move to recycle bin"))],
        ["shell_run"] = [Text("command", "PowerShell command to execute."), Text("workingDirectory", "Optional working directory."), Integer("timeoutSeconds", "Timeout in seconds."), Boolean("runAsAdministrator", "Run PowerShell with administrator privileges. Use only when explicitly needed; it requires approval.")],
        ["web_search"] = [Text("query", "Search query."), Integer("limit", "Maximum number of search results.")],
        ["web_fetch"] = [Text("url", "HTTP or HTTPS URL to fetch."), Integer("maxBytes", "Maximum bytes to read.")],
        ["open_windows_settings"] = [Text("uri", "Windows settings URI, starting with ms-settings:.")],
        ["save_hosts"] = [Text("content", "Full hosts file content.")],
        ["set_env"] = [Text("target", "Environment target: User or Machine."), Text("name", "Variable name."), Text("value", "Variable value.")],
        ["delete_env"] = [Text("target", "Environment target: User or Machine."), Text("name", "Variable name.")],
        ["file_lock_delete_recycle"] = [Text("path", "File or directory path to move to recycle bin."), Boolean("isDirectory", "Whether the path is a directory.")],
        ["file_lock_kill_delete"] = [Text("path", "File or directory path to delete after killing locking processes."), Boolean("isDirectory", "Whether the path is a directory."), IntegerArray("processIds", "Optional process IDs to kill.")],
        ["kill_port_owner"] = [Integer("processId", "Process ID to terminate."), Integer("port", "Optional port used to confirm the target process.")]
    };

    public static readonly JsonElement EmptyObjectSchema = CreateSchema([], []);

    public static JsonElement ForTool(string toolId)
    {
        var properties = PropertiesByTool.TryGetValue(toolId, out var foundProperties)
            ? foundProperties
            : [];
        var required = RequiredByTool.TryGetValue(toolId, out var foundRequired)
            ? foundRequired
            : [];
        return CreateSchema(properties, required);
    }

    private static JsonElement CreateSchema(IReadOnlyList<ToolProperty> properties, IReadOnlyList<string> required)
        => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = properties.ToDictionary(
                property => property.Name,
                property => property.Schema,
                StringComparer.Ordinal),
            required
        });

    private static ToolProperty Text(string name, string description)
        => new(name, new Dictionary<string, object?>
        {
            ["type"] = "string",
            ["description"] = description
        });

    private static string PathDescription(string purpose)
        => purpose + ". Supports absolute paths, %USERPROFILE%, ~, and common user-folder aliases such as Desktop/Documents/Downloads or 桌面/文档/下载.";

    private static ToolProperty Integer(string name, string description)
        => new(name, new Dictionary<string, object?>
        {
            ["type"] = "integer",
            ["description"] = description
        });

    private static ToolProperty Boolean(string name, string description)
        => new(name, new Dictionary<string, object?>
        {
            ["type"] = "boolean",
            ["description"] = description
        });

    private static ToolProperty IntegerArray(string name, string description)
        => new(name, new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new Dictionary<string, object?> { ["type"] = "integer" }
        });

    private sealed record ToolProperty(string Name, IReadOnlyDictionary<string, object?> Schema);
}
