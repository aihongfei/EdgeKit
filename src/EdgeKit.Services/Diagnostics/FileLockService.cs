using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EdgeKit.Services.SystemOperations;
using VBFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;

namespace EdgeKit.Services.Diagnostics;

/// <summary>Finds file handles that can block deleting a file or folder.</summary>
public sealed class FileLockService
{
    public const string ElevatedActionArgument = "--edgekit-filelock-action";

    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int StatusSuccess = 0;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DuplicateCloseSource = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint FileTypeDisk = 0x0001;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ElevatedOperationService? _elevation;

    public FileLockService()
    {
    }

    public FileLockService(ElevatedOperationService elevation)
    {
        _elevation = elevation;
    }

    public Task<FileLockSnapshot> ScanAsync(string targetPath, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(targetPath, cancellationToken), cancellationToken);

    public Task<ToolActionResult> DeleteToRecycleBinAsync(
        string targetPath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run(() => DeleteToRecycleBin(targetPath, isDirectory, allowElevation: true), cancellationToken);

    public Task<ToolActionResult> KillProcessesAndDeleteAsync(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<FileLockEntry> entries,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => KillProcessesAndDelete(
                targetPath,
                isDirectory,
                entries.Select(e => e.ProcessId).Distinct().ToArray(),
                allowElevation: true),
            cancellationToken);

    public Task<ToolActionResult> CloseHandlesAndDeleteAsync(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<FileLockEntry> entries,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => CloseHandlesAndDelete(
                targetPath,
                isDirectory,
                entries.Select(e => new FileLockHandleSelection(e.ProcessId, e.HandleValue, e.FilePath)).ToArray(),
                allowElevation: true),
            cancellationToken);

    public string BuildReport(FileLockSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EdgeKit 文件锁定检测");
        builder.AppendLine($"路径: {snapshot.TargetPath}");
        builder.AppendLine($"类型: {(snapshot.IsDirectory ? "文件夹" : "文件")}");
        builder.AppendLine($"刷新时间: {snapshot.RefreshedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"管理员权限: {(snapshot.IsAdministrator ? "是" : "否")}");
        builder.AppendLine($"占用句柄: {snapshot.Entries.Count}");
        if (snapshot.InaccessibleProcessCount > 0)
        {
            builder.AppendLine($"不可访问进程: {snapshot.InaccessibleProcessCount}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Warning))
        {
            builder.AppendLine($"提示: {snapshot.Warning}");
        }

        foreach (var entry in snapshot.Entries)
        {
            builder.AppendLine(entry.ToReportText());
        }

        return builder.ToString().TrimEnd();
    }

    public static int ExecuteElevatedActionCommand(string requestPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(requestPath);
            var pending = Path.GetFullPath(PendingDirectory);
            if (!fullPath.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                WriteResult(fullPath, "请求路径不在 EdgeKit 文件锁定操作目录内");
                return 2;
            }

            if (!File.Exists(fullPath))
            {
                WriteResult(fullPath, "文件锁定操作请求不存在");
                return 3;
            }

            var request = JsonSerializer.Deserialize<ElevatedFileLockRequest>(
                File.ReadAllText(fullPath, Encoding.UTF8),
                JsonOptions);
            if (request is null)
            {
                WriteResult(fullPath, "文件锁定操作请求无法解析");
                return 4;
            }

            var service = new FileLockService();
            var result = service.ExecuteAsAdministrator(
                request.Action,
                request.TargetPath,
                request.IsDirectory,
                request.ProcessIds,
                request.Handles);

            WriteResult(fullPath, result.Message);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            try
            {
                WriteResult(requestPath, ex.Message);
            }
            catch
            {
                // Best effort only; the caller still receives the exit code.
            }

            return 1;
        }
    }

    public ToolActionResult ExecuteAsAdministrator(
        string action,
        string targetPath,
        bool isDirectory,
        IReadOnlyList<int> processIds,
        IReadOnlyList<FileLockHandleSelection> handles)
        => action switch
        {
            ElevatedFileLockRequest.DeleteAction => DeleteToRecycleBin(
                targetPath,
                isDirectory,
                allowElevation: false),
            ElevatedFileLockRequest.KillAction => KillProcessesAndDelete(
                targetPath,
                isDirectory,
                processIds,
                allowElevation: false),
            ElevatedFileLockRequest.CloseHandleAction => CloseHandlesAndDelete(
                targetPath,
                isDirectory,
                handles,
                allowElevation: false),
            _ => new ToolActionResult(false, "未知的文件锁定操作")
        };

    private FileLockSnapshot Scan(string targetPath, CancellationToken cancellationToken)
    {
        if (!TryNormalizeTarget(targetPath, preferredDirectory: null, out var target, out var error))
        {
            return new FileLockSnapshot(
                targetPath?.Trim() ?? string.Empty,
                DateTime.Now,
                IsAdministrator(),
                false,
                Array.Empty<FileLockEntry>(),
                0,
                error);
        }

        TryEnableDebugPrivilege();

        var entries = new List<FileLockEntry>();
        var inaccessiblePids = new HashSet<int>();
        var processHandles = new Dictionary<int, nint>();
        var processInfo = new Dictionary<int, (string Name, string Path)>();

        try
        {
            foreach (var handle in QuerySystemHandles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (handle.ProcessId <= 0)
                {
                    continue;
                }

                if (!processHandles.TryGetValue(handle.ProcessId, out var processHandle))
                {
                    processHandle = OpenProcess(ProcessDupHandle, false, (uint)handle.ProcessId);
                    processHandles[handle.ProcessId] = processHandle;
                    if (processHandle == nint.Zero)
                    {
                        inaccessiblePids.Add(handle.ProcessId);
                    }
                }

                if (processHandle == nint.Zero
                    || !TryGetPathFromProcessHandle(processHandle, handle.HandleValue, out var filePath))
                {
                    continue;
                }

                if (!PathMatchesTarget(filePath, target))
                {
                    continue;
                }

                if (!processInfo.TryGetValue(handle.ProcessId, out var info))
                {
                    info = ReadProcessInfo(handle.ProcessId);
                    processInfo[handle.ProcessId] = info;
                }

                entries.Add(new FileLockEntry(
                    handle.ProcessId,
                    info.Name,
                    info.Path,
                    handle.HandleValue,
                    filePath,
                    handle.GrantedAccess,
                    handle.ObjectTypeIndex));
            }
        }
        finally
        {
            foreach (var handle in processHandles.Values)
            {
                if (handle != nint.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        var ordered = entries
            .OrderBy(e => e.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.ProcessId)
            .ThenBy(e => e.FilePath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.HandleValue)
            .ToArray();

        var warnings = new List<string>();
        if (inaccessiblePids.Count > 0)
        {
            warnings.Add("部分系统或受保护进程不可访问，管理员运行可看到更多结果");
        }

        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            warnings.Add("当前为 x86 进程，可能漏检部分 64 位进程句柄，建议使用 x64/ARM64 版本");
        }

        return new FileLockSnapshot(
            target.Path,
            DateTime.Now,
            IsAdministrator(),
            target.IsDirectory,
            ordered,
            inaccessiblePids.Count,
            string.Join(Environment.NewLine, warnings));
    }

    private ToolActionResult DeleteToRecycleBin(string targetPath, bool isDirectory, bool allowElevation)
    {
        if (!TryNormalizeTarget(targetPath, isDirectory, out var target, out var error))
        {
            return new ToolActionResult(false, error);
        }

        try
        {
            DeleteTargetToRecycleBin(target);
            return new ToolActionResult(true, "已移入回收站");
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            if (allowElevation && !IsAdministrator())
            {
                return RunElevatedAction(
                    ElevatedFileLockRequest.DeleteAction,
                    target.Path,
                    target.IsDirectory,
                    Array.Empty<int>(),
                    Array.Empty<FileLockHandleSelection>());
            }

            return new ToolActionResult(false, "删除失败：" + ex.Message);
        }
    }

    private ToolActionResult KillProcessesAndDelete(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<int> processIds,
        bool allowElevation)
    {
        if (!TryNormalizeTarget(targetPath, isDirectory, out var target, out var error))
        {
            return new ToolActionResult(false, error);
        }

        var ids = processIds
            .Where(CanTouchProcess)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new ToolActionResult(false, "没有可结束的占用进程");
        }

        var killed = 0;
        var failures = new List<string>();
        foreach (var processId in ids)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: false);
                process.WaitForExit(2500);
                killed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"PID {processId}: {ex.Message}");
            }
        }

        try
        {
            DeleteTargetToRecycleBin(target);
            return new ToolActionResult(
                true,
                failures.Count == 0
                    ? $"已结束 {killed} 个进程并移入回收站"
                    : $"已结束 {killed} 个进程并移入回收站；部分进程结束失败");
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            if (allowElevation && !IsAdministrator())
            {
                return RunElevatedAction(
                    ElevatedFileLockRequest.KillAction,
                    target.Path,
                    target.IsDirectory,
                    ids,
                    Array.Empty<FileLockHandleSelection>());
            }

            var prefix = killed > 0 ? $"已结束 {killed} 个进程，但" : string.Empty;
            var detail = failures.Count == 0 ? string.Empty : "；" + string.Join("；", failures.Take(3));
            return new ToolActionResult(false, $"{prefix}删除失败：{ex.Message}{detail}");
        }
    }

    private ToolActionResult CloseHandlesAndDelete(
        string targetPath,
        bool isDirectory,
        IReadOnlyList<FileLockHandleSelection> handles,
        bool allowElevation)
    {
        if (!TryNormalizeTarget(targetPath, isDirectory, out var target, out var error))
        {
            return new ToolActionResult(false, error);
        }

        var selections = handles
            .Where(h => CanTouchProcess(h.ProcessId))
            .DistinctBy(h => (h.ProcessId, h.HandleValue))
            .ToArray();
        if (selections.Length == 0)
        {
            return new ToolActionResult(false, "没有可关闭的文件句柄");
        }

        TryEnableDebugPrivilege();

        var closed = 0;
        var failures = new List<string>();
        foreach (var selection in selections)
        {
            var close = CloseRemoteHandle(selection, target);
            if (close.Success)
            {
                closed++;
            }
            else
            {
                failures.Add(close.Message);
            }
        }

        try
        {
            DeleteTargetToRecycleBin(target);
            return new ToolActionResult(
                true,
                failures.Count == 0
                    ? $"已关闭 {closed} 个句柄并移入回收站"
                    : $"已关闭 {closed} 个句柄并移入回收站；部分句柄关闭失败");
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            if (allowElevation && !IsAdministrator())
            {
                return RunElevatedAction(
                    ElevatedFileLockRequest.CloseHandleAction,
                    target.Path,
                    target.IsDirectory,
                    Array.Empty<int>(),
                    selections);
            }

            var detail = failures.Count == 0 ? string.Empty : "；" + string.Join("；", failures.Take(3));
            return new ToolActionResult(false, $"已关闭 {closed} 个句柄，但删除失败：{ex.Message}{detail}");
        }
    }

    private ToolActionResult CloseRemoteHandle(FileLockHandleSelection selection, TargetInfo target)
    {
        var processHandle = OpenProcess(ProcessDupHandle, false, (uint)selection.ProcessId);
        if (processHandle == nint.Zero)
        {
            return new ToolActionResult(false, $"PID {selection.ProcessId} 不可访问");
        }

        try
        {
            if (!TryGetPathFromProcessHandle(processHandle, selection.HandleValue, out var currentPath))
            {
                return new ToolActionResult(false, $"PID {selection.ProcessId} 句柄 {FormatHandle(selection.HandleValue)} 已不可验证");
            }

            if (!PathMatchesTarget(currentPath, target)
                || !PathsEqual(currentPath, selection.FilePath))
            {
                return new ToolActionResult(false, $"PID {selection.ProcessId} 句柄 {FormatHandle(selection.HandleValue)} 已变化，已跳过");
            }

            var sourceHandle = new nint(unchecked((long)selection.HandleValue));
            var ok = DuplicateHandle(
                processHandle,
                sourceHandle,
                GetCurrentProcess(),
                out var duplicated,
                0,
                false,
                DuplicateCloseSource | DuplicateSameAccess);

            if (duplicated != nint.Zero)
            {
                CloseHandle(duplicated);
            }

            return ok
                ? new ToolActionResult(true, $"已关闭 PID {selection.ProcessId} 句柄 {FormatHandle(selection.HandleValue)}")
                : new ToolActionResult(false, $"PID {selection.ProcessId} 句柄 {FormatHandle(selection.HandleValue)} 关闭失败");
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private ToolActionResult RunElevatedAction(
        string action,
        string targetPath,
        bool isDirectory,
        IReadOnlyList<int> processIds,
        IReadOnlyList<FileLockHandleSelection> handles)
    {
        if (_elevation is null)
        {
            return new ToolActionResult(false, "需要管理员权限，但提权服务不可用");
        }

        var result = _elevation.RunElevatedAsync(
            ElevatedOperationIds.FileLockAction,
            new FileLockActionPayload(
                action,
                targetPath,
                isDirectory,
                processIds,
                handles),
            "diagnostics:filelock",
            "文件锁定操作 " + targetPath).GetAwaiter().GetResult();
        return result.ToToolActionResult();
    }

    private static IReadOnlyList<SystemHandleEntry> QuerySystemHandles()
    {
        var length = 1024 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQuerySystemInformation(
                    SystemExtendedHandleInformation,
                    buffer,
                    length,
                    out var returnLength);

                if (status is StatusInfoLengthMismatch or StatusBufferOverflow or StatusBufferTooSmall)
                {
                    length = Math.Max(length * 2, returnLength + 4096);
                    continue;
                }

                if (status != StatusSuccess)
                {
                    return Array.Empty<SystemHandleEntry>();
                }

                var count = Marshal.ReadIntPtr(buffer).ToInt64();
                var rowSize = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
                var rowPtr = nint.Add(buffer, nint.Size * 2);
                var result = new List<SystemHandleEntry>((int)Math.Min(count, 200_000));

                for (var i = 0L; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<SystemHandleTableEntryInfoEx>(nint.Add(rowPtr, checked((int)(i * rowSize))));
                    var pid = unchecked((int)row.UniqueProcessId.ToInt64());
                    var handleValue = unchecked((ulong)row.HandleValue.ToInt64());
                    result.Add(new SystemHandleEntry(pid, handleValue, row.GrantedAccess, row.ObjectTypeIndex));
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return Array.Empty<SystemHandleEntry>();
    }

    private static bool TryGetPathFromProcessHandle(nint processHandle, ulong handleValue, out string filePath)
    {
        filePath = string.Empty;
        var sourceHandle = new nint(unchecked((long)handleValue));
        var ok = DuplicateHandle(
            processHandle,
            sourceHandle,
            GetCurrentProcess(),
            out var duplicated,
            0,
            false,
            DuplicateSameAccess);

        if (!ok || duplicated == nint.Zero)
        {
            return false;
        }

        try
        {
            if (GetFileType(duplicated) != FileTypeDisk)
            {
                return false;
            }

            filePath = ReadFinalPath(duplicated);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = DevicePathMapper.ToDosPath(ReadObjectName(duplicated));
            }

            filePath = NormalizeHandlePath(filePath);
            return !string.IsNullOrWhiteSpace(filePath);
        }
        finally
        {
            CloseHandle(duplicated);
        }
    }

    private static string ReadFinalPath(nint handle)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
            {
                return string.Empty;
            }

            if (length < builder.Capacity)
            {
                return builder.ToString();
            }

            capacity = checked((int)length + 1);
        }

        return string.Empty;
    }

    private static string ReadObjectName(nint handle)
    {
        var length = 4096;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryObject(handle, ObjectNameInformation, buffer, length, out var returnLength);
                if (status is StatusInfoLengthMismatch or StatusBufferOverflow or StatusBufferTooSmall)
                {
                    length = Math.Max(length * 2, returnLength + 512);
                    continue;
                }

                if (status != StatusSuccess)
                {
                    return string.Empty;
                }

                var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                return value.Length == 0 || value.Buffer == nint.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return string.Empty;
    }

    private static (string Name, string Path) ReadProcessInfo(int processId)
    {
        if (processId == 4)
        {
            return ("System", string.Empty);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            var path = string.Empty;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                path = QueryProcessImagePath(processId);
            }

            return (name, path);
        }
        catch
        {
            return ("不可用", string.Empty);
        }
    }

    private static string QueryProcessImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size)
                ? builder.ToString()
                : string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void DeleteTargetToRecycleBin(TargetInfo target)
    {
        if (target.IsDirectory)
        {
            VBFileSystem.DeleteDirectory(
                target.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }

        VBFileSystem.DeleteFile(
            target.Path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private static bool TryNormalizeTarget(
        string? rawPath,
        bool? preferredDirectory,
        out TargetInfo target,
        out string error)
    {
        target = default!;
        error = string.Empty;

        var value = Environment.ExpandEnvironmentVariables((rawPath ?? string.Empty).Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入文件或文件夹路径";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(NormalizeHandlePath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径无效：" + ex.Message;
            return false;
        }

        if (File.Exists(fullPath))
        {
            target = new TargetInfo(fullPath, false);
            return true;
        }

        if (Directory.Exists(fullPath))
        {
            target = new TargetInfo(TrimTrailingSeparators(fullPath), true);
            return true;
        }

        if (preferredDirectory is not null)
        {
            target = new TargetInfo(
                preferredDirectory.Value ? TrimTrailingSeparators(fullPath) : fullPath,
                preferredDirectory.Value);
            error = "路径不存在";
            return false;
        }

        error = "路径不存在";
        return false;
    }

    private static bool PathMatchesTarget(string candidatePath, TargetInfo target)
    {
        var candidate = NormalizeForComparison(candidatePath);
        var targetPath = NormalizeForComparison(target.Path);

        if (!target.IsDirectory)
        {
            return string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(targetPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
        => TrimTrailingSeparators(NormalizeHandlePath(path));

    private static string NormalizeHandlePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Trim();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        return value;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        while (path.Length > (root?.Length ?? 0)
            && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            path = path[..^1];
        }

        return path;
    }

    private static bool CanTouchProcess(int processId)
        => processId > 0 && processId != 4 && processId != Environment.ProcessId;

    private static bool IsFileOperationException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException;

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                return;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            _ = AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string FormatHandle(ulong handle)
        => "0x" + handle.ToString("X", CultureInfo.InvariantCulture);

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void WriteResult(string requestPath, string message)
        => File.WriteAllText(GetResultPath(requestPath), message, Encoding.UTF8);

    private static string ReadResult(string requestPath)
        => File.Exists(GetResultPath(requestPath))
            ? File.ReadAllText(GetResultPath(requestPath), Encoding.UTF8)
            : string.Empty;

    private static string GetResultPath(string requestPath)
        => requestPath + ".result";

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

    private static string EdgeKitDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeKit");

    private static string PendingDirectory
        => Path.Combine(EdgeKitDirectory, "pending-filelock");

    private sealed record TargetInfo(string Path, bool IsDirectory);

    private sealed record SystemHandleEntry(
        int ProcessId,
        ulong HandleValue,
        uint GrantedAccess,
        ushort ObjectTypeIndex);

    private sealed class ElevatedFileLockRequest
    {
        public const string DeleteAction = "delete";
        public const string KillAction = "kill-processes";
        public const string CloseHandleAction = "close-handles";

        public string Action { get; set; } = string.Empty;

        public string TargetPath { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public List<int> ProcessIds { get; set; } = new();

        public List<FileLockHandleSelection> Handles { get; set; } = new();
    }

    private static class DevicePathMapper
    {
        private static readonly Lazy<IReadOnlyList<(string Device, string Drive)>> Mappings = new(BuildMappings);

        public static string ToDosPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            foreach (var (device, drive) in Mappings.Value)
            {
                if (path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                {
                    return drive + path[device.Length..];
                }
            }

            return path;
        }

        private static IReadOnlyList<(string Device, string Drive)> BuildMappings()
        {
            var result = new List<(string Device, string Drive)>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                var name = drive.Name.TrimEnd('\\');
                var builder = new StringBuilder(1024);
                if (QueryDosDevice(name, builder, builder.Capacity) == 0)
                {
                    continue;
                }

                var device = builder.ToString();
                if (!string.IsNullOrWhiteSpace(device))
                {
                    result.Add((device, name));
                }
            }

            return result
                .OrderByDescending(m => m.Device.Length)
                .ToArray();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfoEx
    {
        public nint Object;
        public nint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        nint systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        nint handle,
        int objectInformationClass,
        nint objectInformation,
        int objectInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        nint sourceProcessHandle,
        nint sourceHandle,
        nint targetProcessHandle,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        nint file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        nint process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);
}

public sealed record FileLockSnapshot(
    string TargetPath,
    DateTime RefreshedAt,
    bool IsAdministrator,
    bool IsDirectory,
    IReadOnlyList<FileLockEntry> Entries,
    int InaccessibleProcessCount,
    string Warning)
{
    public string SummaryText
        => $"{Entries.Count} 个占用句柄 · {(IsDirectory ? "文件夹" : "文件")} · {(IsAdministrator ? "管理员" : "普通权限")} · {RefreshedAt:HH:mm:ss}";
}

public sealed record FileLockEntry(
    int ProcessId,
    string ProcessName,
    string ProcessPath,
    ulong HandleValue,
    string FilePath,
    uint GrantedAccess,
    ushort ObjectTypeIndex)
{
    public bool IsSelected { get; set; }

    public string ProcessIdText => ProcessId <= 0 ? "不可用" : ProcessId.ToString(CultureInfo.InvariantCulture);

    public string HandleText => "0x" + HandleValue.ToString("X", CultureInfo.InvariantCulture);

    public string AccessText => "0x" + GrantedAccess.ToString("X", CultureInfo.InvariantCulture);

    public string TypeText => ObjectTypeIndex.ToString(CultureInfo.InvariantCulture);

    public string ProcessDirectory
        => string.IsNullOrWhiteSpace(ProcessPath) ? string.Empty : Path.GetDirectoryName(ProcessPath) ?? string.Empty;

    public string Title => $"{ProcessName} · PID {ProcessIdText} · {HandleText}";

    public string Detail => $"访问 {AccessText} · 类型 {TypeText}";

    public bool CanKill => ProcessId > 0 && ProcessId != 4 && ProcessId != Environment.ProcessId;

    public bool CanCloseHandle => CanKill;

    public bool CanOpenDirectory => !string.IsNullOrWhiteSpace(ProcessDirectory);

    public string ToReportText()
        => $"PID {ProcessIdText} {ProcessName} {HandleText} Access {AccessText} File {FilePath} Process {ProcessPath}".TrimEnd();
}

public sealed record FileLockHandleSelection(int ProcessId, ulong HandleValue, string FilePath);
