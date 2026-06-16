using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EdgeKit.Services.Diagnostics;

namespace EdgeKit.Services.SystemOperations;

public interface IElevatedOperationHandler
{
    string OperationId { get; }

    ElevatedOperationResult Execute(JsonElement payload, CancellationToken cancellationToken = default);
}

public sealed class ElevatedOperationService
{
    public const string ElevatedOperationArgument = "--edgekit-elevated-operation";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IReadOnlyDictionary<string, IElevatedOperationHandler> _handlers;

    public ElevatedOperationService(IEnumerable<IElevatedOperationHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.OperationId, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAdministrator => IsCurrentProcessAdministrator();

    public async Task<ElevatedOperationResult> ExecuteOrElevateAsync(
        ElevatedOperation operation,
        Func<CancellationToken, Task<ElevatedOperationResult>> action,
        CancellationToken cancellationToken = default)
    {
        if (operation.RequireElevation && !IsAdministrator)
        {
            return await RunElevatedAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        if (!IsAdministrator)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsAccessDenied(ex) && !IsAdministrator)
            {
                return await RunElevatedAsync(operation, cancellationToken).ConfigureAwait(false);
            }
        }

        return await action(cancellationToken).ConfigureAwait(false);
    }

    public Task<ElevatedOperationResult> RunElevatedAsync(
        string operationId,
        object payload,
        string requestedBy,
        string summary,
        int timeoutSeconds = 120,
        bool requiresInteractiveDesktop = false,
        CancellationToken cancellationToken = default)
        => RunElevatedAsync(
            new ElevatedOperation(
                operationId,
                JsonSerializer.SerializeToElement(payload, JsonOptions),
                requestedBy,
                summary,
                DateTimeOffset.Now,
                timeoutSeconds,
                requiresInteractiveDesktop,
                true),
            cancellationToken);

    public async Task<ElevatedOperationResult> RunElevatedAsync(
        ElevatedOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (IsAdministrator)
        {
            return Execute(operation, cancellationToken);
        }

        try
        {
            Directory.CreateDirectory(PendingDirectory);
            var requestPath = Path.Combine(
                PendingDirectory,
                $"operation-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");

            var request = new ElevatedOperationRequest(
                operation.OperationId,
                operation.Payload,
                operation.RequestedBy,
                operation.Summary,
                operation.CreatedAt,
                operation.TimeoutSeconds,
                operation.RequiresInteractiveDesktop);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return ElevatedOperationResult.Fail("无法定位 EdgeKit 可执行文件", "missing_process_path");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = $"{ElevatedOperationArgument} {QuoteArgument(requestPath)}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                return ElevatedOperationResult.Fail("无法启动管理员操作进程", "start_failed");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(operation.TimeoutSeconds, 1, 600)));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return ElevatedOperationResult.Fail("管理员操作超时", "timeout");
            }

            var result = await ReadResultAsync(requestPath, cancellationToken).ConfigureAwait(false);
            TryDelete(requestPath);
            TryDelete(GetResultPath(requestPath));

            if (result is not null)
            {
                return result with { ExitCode = result.ExitCode ?? process.ExitCode };
            }

            return process.ExitCode == 0
                ? ElevatedOperationResult.Ok("管理员操作已完成", exitCode: process.ExitCode)
                : ElevatedOperationResult.Fail("管理员操作失败，未返回结果", "missing_result", process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevatedOperationResult.Fail("已取消管理员授权", "uac_cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or Win32Exception or JsonException)
        {
            return ElevatedOperationResult.Fail("管理员操作失败：" + ex.Message, "elevation_failed");
        }
    }

    public int ExecuteInternalCommand(string requestPath)
    {
        var fullPath = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(requestPath);
            var pending = Path.GetFullPath(PendingDirectory);
            if (!fullPath.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                WriteResult(fullPath, ElevatedOperationResult.Fail("请求路径不在 EdgeKit 提权操作目录内", "invalid_request_path"));
                return 2;
            }

            if (!File.Exists(fullPath))
            {
                WriteResult(fullPath, ElevatedOperationResult.Fail("提权操作请求不存在", "request_missing"));
                return 3;
            }

            var request = JsonSerializer.Deserialize<ElevatedOperationRequest>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.OperationId))
            {
                WriteResult(fullPath, ElevatedOperationResult.Fail("提权操作请求无效", "invalid_request"));
                return 4;
            }

            var operation = new ElevatedOperation(
                request.OperationId,
                request.Payload,
                request.RequestedBy,
                request.Summary,
                request.CreatedAt,
                request.TimeoutSeconds,
                request.RequiresInteractiveDesktop,
                true);
            var result = Execute(operation);
            WriteResult(fullPath, result);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException or InvalidOperationException)
        {
            try
            {
                WriteResult(
                    string.IsNullOrWhiteSpace(fullPath) ? requestPath : fullPath,
                    ElevatedOperationResult.Fail(ex.Message, "internal_command_failed"));
            }
            catch
            {
                // Best effort only; the parent process still receives the exit code.
            }

            return 1;
        }
    }

    public ElevatedOperationResult Execute(ElevatedOperation operation, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(operation.OperationId, out var handler))
        {
            return ElevatedOperationResult.Fail("未知的提权操作: " + operation.OperationId, "unknown_operation");
        }

        try
        {
            return handler.Execute(operation.Payload, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return ElevatedOperationResult.Fail(ex.Message, "operation_failed");
        }
    }

    public static bool IsAccessDenied(Exception ex)
    {
        if (ex is UnauthorizedAccessException or SecurityException)
        {
            return true;
        }

        if (ex is IOException io)
        {
            const int errorAccessDenied = unchecked((int)0x80070005);
            const int errorSharingViolation = unchecked((int)0x80070020);
            return io.HResult is errorAccessDenied or errorSharingViolation
                || io.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || io.Message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task<ElevatedOperationResult?> ReadResultAsync(string requestPath, CancellationToken cancellationToken)
    {
        var resultPath = GetResultPath(requestPath);
        if (!File.Exists(resultPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(resultPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ElevatedOperationResult>(json, JsonOptions);
    }

    private static void WriteResult(string requestPath, ElevatedOperationResult result)
    {
        var directory = Path.GetDirectoryName(requestPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(GetResultPath(requestPath), JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
    }

    private static string GetResultPath(string requestPath)
        => requestPath + ".result.json";

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort.
        }
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

    private static bool IsCurrentProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string EdgeKitDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeKit");

    private static string PendingDirectory
        => Path.Combine(EdgeKitDirectory, "elevation", "pending");
}

public sealed record ElevatedOperation(
    string OperationId,
    JsonElement Payload,
    string RequestedBy,
    string Summary,
    DateTimeOffset CreatedAt,
    int TimeoutSeconds,
    bool RequiresInteractiveDesktop,
    bool RequireElevation = false);

public sealed record ElevatedOperationRequest(
    string OperationId,
    JsonElement Payload,
    string RequestedBy,
    string Summary,
    DateTimeOffset CreatedAt,
    int TimeoutSeconds,
    bool RequiresInteractiveDesktop);

public sealed record ElevatedOperationResult(
    bool Success,
    string Message,
    string Stdout,
    string Stderr,
    int? ExitCode,
    string ErrorCode)
{
    public static ElevatedOperationResult Ok(
        string message,
        string stdout = "",
        string stderr = "",
        int? exitCode = null)
        => new(true, message, stdout, stderr, exitCode, string.Empty);

    public static ElevatedOperationResult Fail(
        string message,
        string errorCode,
        int? exitCode = null,
        string stdout = "",
        string stderr = "")
        => new(false, message, stdout, stderr, exitCode, errorCode);

    public ToolActionResult ToToolActionResult()
        => new(Success, Message);
}
