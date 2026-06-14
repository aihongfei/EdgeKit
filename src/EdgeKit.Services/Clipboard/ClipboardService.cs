using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.Core.Clipboard;
using EdgeKit.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRtClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace EdgeKit.Services.Clipboard;

/// <summary>
/// 剪贴板捕获服务。监听系统剪贴板变化，读取内容并识别类型（文本/URL/JSON/文件路径/图片/文件列表），
/// 去重后写入仓储，并调用 <see cref="IClipboardClassifier"/> 异步回填分组（预留大模型入口）。
/// 监听基于 <see cref="ClipboardListener"/>（窗口消息），读取使用 WinRT Clipboard API（须在 UI 线程）。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    // 摘要最大长度，避免超长文本占用 UI。
    private const int PreviewMaxLength = 200;

    // 单条捕获文本上限，超出截断存储（防止超大文本拖慢库与 UI）。
    private const int ContentMaxLength = 100_000;

    private readonly IClipboardRepository _repository;
    private readonly IClipboardClassifier _classifier;
    private readonly string _imageDirectory;
    private readonly ClipboardListener _listener = new();

    // 自身写剪贴板（点击复制）时置位，跳过下一次捕获，避免回写抖动。
    private bool _suppressNext;

    // 是否暂停记录（隐私：可由设置控制）。
    private bool _paused;

    // 最近一次成功捕获的系统剪贴板序号，用于跳过同一次复制产生的重复通知。
    private uint _lastCapturedSequence;

    public ClipboardService(
        IClipboardRepository repository,
        IClipboardClassifier classifier,
        string imageDirectory)
    {
        _repository = repository;
        _classifier = classifier;
        _imageDirectory = imageDirectory;
        Directory.CreateDirectory(_imageDirectory);
    }

    /// <summary>暂停/恢复记录（隐私开关）。</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>在指定窗口上启动剪贴板监听（须在 UI 线程调用）。</summary>
    public void Start(nint hwnd)
    {
        _listener.Changed += OnClipboardChanged;
        _listener.Start(hwnd);
    }

    /// <summary>
    /// 在自身写剪贴板前调用，跳过紧接着的一次捕获，避免把自己复制的内容重复记录。
    /// </summary>
    public void SuppressNextCapture() => _suppressNext = true;

    private async void OnClipboardChanged(object? sender, EventArgs e)
    {
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }

        if (_paused)
        {
            return;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        var source = CaptureSource();
        await CaptureWithRetryAsync(sequence, source).ConfigureAwait(true);
    }

    private async Task CaptureWithRetryAsync(uint sequence, ClipboardSource source)
    {
        // 一些应用（尤其右键菜单复制、富文本编辑器、浏览器）会短暂占用剪贴板
        // 或分批写入格式；稍等并重试可以避免把这类复制动作直接漏掉。
        var delays = new[] { 50, 120, 250, 500 };
        Exception? lastError = null;

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            await Task.Delay(delays[attempt]).ConfigureAwait(true);

            if (sequence != 0 && sequence == _lastCapturedSequence)
            {
                return;
            }

            try
            {
                if (await CaptureAsync(source).ConfigureAwait(true))
                {
                    if (sequence != 0)
                    {
                        _lastCapturedSequence = sequence;
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                Debug.WriteLine($"剪贴板捕获失败，准备重试 attempt={attempt + 1} sequence={sequence}: {ex}");
            }
        }

        if (lastError is not null)
        {
            Debug.WriteLine($"剪贴板捕获重试后仍失败 sequence={sequence}: {lastError}");
        }
        else
        {
            Debug.WriteLine($"剪贴板捕获重试后仍未读到支持的格式 sequence={sequence}");
        }
    }

    private async Task<bool> CaptureAsync(ClipboardSource source)
    {
        var content = WinRtClipboard.GetContent();
        var formats = content.AvailableFormats;

        ClipboardItem? item = null;

        // 优先级：文件列表 → 文本 → 位图。富文本复制常同时携带多种格式，
        // 只要有普通文本，就应优先作为文本历史记录下来。
        if (formats.Contains(StandardDataFormats.StorageItems))
        {
            item = await CaptureFilesAsync(content, source).ConfigureAwait(true);
        }

        if (item is null && formats.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            item = CaptureText(text, source);
        }

        if (item is null && formats.Contains(StandardDataFormats.Bitmap))
        {
            item = await CaptureImageAsync(content, source).ConfigureAwait(true);
        }

        if (item is null)
        {
            item = CaptureText(TryReadUnicodeTextFromClipboard(), source);
        }

        if (item is null)
        {
            return false;
        }

        var id = _repository.Add(item);

        // 写入后异步回填分组（大模型入口）。失败静默，不阻塞捕获。
        _ = ClassifyAsync(item with { Id = id });
        return true;
    }

    private async Task ClassifyAsync(ClipboardItem item)
    {
        try
        {
            var groups = _repository.GetGroups();
            if (groups.Count == 0)
            {
                return;
            }

            var groupId = await _classifier
                .SuggestGroupAsync(item, groups, CancellationToken.None)
                .ConfigureAwait(false);

            if (groupId is not null)
            {
                _repository.MoveToGroup(item.Id, groupId);
            }
        }
        catch
        {
            // 分类失败保持未分组。
        }
    }

    private static ClipboardItem? CaptureText(string? raw, ClipboardSource source)
    {
        var text = raw ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > ContentMaxLength)
        {
            text = text[..ContentMaxLength];
        }

        var kind = DetectTextKind(text);
        return new ClipboardItem(
            0,
            kind,
            BuildPreview(text),
            text,
            ImagePath: null,
            Files: null,
            GroupId: null,
            Pinned: false,
            SourceAppName: source.AppName,
            SourceProcessPath: source.ProcessPath,
            Hash: ComputeHash("text:" + text),
            CreatedUtc: DateTime.UtcNow);
    }

    private static string? TryReadUnicodeTextFromClipboard()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return null;
        }

        if (!NativeMethods.OpenClipboard(nint.Zero))
        {
            return null;
        }

        nint handle = nint.Zero;
        nint locked = nint.Zero;

        try
        {
            handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (handle == nint.Zero)
            {
                return null;
            }

            locked = NativeMethods.GlobalLock(handle);
            if (locked == nint.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(locked);
        }
        finally
        {
            if (handle != nint.Zero && locked != nint.Zero)
            {
                NativeMethods.GlobalUnlock(handle);
            }

            NativeMethods.CloseClipboard();
        }
    }

    private async Task<ClipboardItem?> CaptureImageAsync(DataPackageView content, ClipboardSource source)
    {
        var reference = await content.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();

        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        var hash = ComputeHash(bytes);
        var path = Path.Combine(_imageDirectory, hash + GuessImageExtension(content));
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
        }

        return new ClipboardItem(
            0,
            ClipboardItemKind.Image,
            "[图片]",
            string.Empty,
            ImagePath: path,
            Files: null,
            GroupId: null,
            Pinned: false,
            SourceAppName: source.AppName,
            SourceProcessPath: source.ProcessPath,
            Hash: ComputeHash("image:" + hash),
            CreatedUtc: DateTime.UtcNow);
    }

    private async Task<ClipboardItem?> CaptureFilesAsync(DataPackageView content, ClipboardSource source)
    {
        var items = await content.GetStorageItemsAsync();
        var paths = items
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        if (paths.Count == 0)
        {
            return null;
        }

        // 单个路径作为 FilePath，多个作为 Files。
        if (paths.Count == 1)
        {
            var single = paths[0];
            return new ClipboardItem(
                0,
                ClipboardItemKind.FilePath,
                single,
                single,
                ImagePath: null,
                Files: null,
                GroupId: null,
                Pinned: false,
                SourceAppName: source.AppName,
                SourceProcessPath: source.ProcessPath,
                Hash: ComputeHash("path:" + single),
                CreatedUtc: DateTime.UtcNow);
        }

        var preview = string.Join("; ", paths.Select(Path.GetFileName));
        return new ClipboardItem(
            0,
            ClipboardItemKind.Files,
            BuildPreview(preview),
            string.Join('\n', paths),
            ImagePath: null,
            Files: paths,
            GroupId: null,
            Pinned: false,
            SourceAppName: source.AppName,
            SourceProcessPath: source.ProcessPath,
            Hash: ComputeHash("files:" + string.Join('|', paths)),
            CreatedUtc: DateTime.UtcNow);
    }

    private static ClipboardSource CaptureSource()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return ClipboardSource.Empty;
            }

            using var process = Process.GetProcessById((int)pid);
            var appName = process.ProcessName ?? string.Empty;
            var processPath = string.Empty;
            try
            {
                processPath = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                // 受保护进程可能不允许读取 MainModule，保留进程名即可。
            }

            return new ClipboardSource(appName, processPath);
        }
        catch
        {
            return ClipboardSource.Empty;
        }
    }

    private readonly record struct ClipboardSource(string AppName, string ProcessPath)
    {
        public static ClipboardSource Empty { get; } = new(string.Empty, string.Empty);
    }

    private static ClipboardItemKind DetectTextKind(string text)
    {
        var trimmed = text.Trim();

        if (LooksLikeUrl(trimmed))
        {
            return ClipboardItemKind.Url;
        }

        if (LooksLikeJson(trimmed))
        {
            return ClipboardItemKind.Json;
        }

        if (LooksLikeFilePath(trimmed))
        {
            return ClipboardItemKind.FilePath;
        }

        return ClipboardItemKind.Text;
    }

    private static bool LooksLikeUrl(string text)
        => (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
           && !text.Contains(' ', StringComparison.Ordinal)
           && Uri.TryCreate(text, UriKind.Absolute, out _);

    private static bool LooksLikeJson(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        var first = text[0];
        var last = text[^1];
        if (!((first == '{' && last == '}') || (first == '[' && last == ']')))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeFilePath(string text)
    {
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        // 形如 C:\... 或 \\server\share 的本地/网络路径。
        var isDrivePath = text.Length >= 3
            && char.IsLetter(text[0])
            && text[1] == ':'
            && (text[2] == '\\' || text[2] == '/');
        var isUncPath = text.StartsWith(@"\\", StringComparison.Ordinal);

        return isDrivePath || isUncPath;
    }

    private static string BuildPreview(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > PreviewMaxLength
            ? single[..PreviewMaxLength] + "…"
            : single;
    }

    private static string GuessImageExtension(DataPackageView content)
    {
        // WinRT 位图统一以 PNG 读取存储。
        _ = content;
        return ".png";
    }

    private static string ComputeHash(string input)
        => ComputeHash(Encoding.UTF8.GetBytes(input));

    private static string ComputeHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _listener.Changed -= OnClipboardChanged;
        _listener.Dispose();
    }
}
