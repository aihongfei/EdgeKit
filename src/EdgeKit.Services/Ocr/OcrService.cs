using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EdgeKit.Services.Ocr;

/// <summary>
/// 基于 Windows.Media.Ocr 的本地 OCR 实现。完全离线，无需额外 NuGet。
/// 调用方应在 UI 线程调用 RecognizeAsync（WinRT OCR 引擎要求 STA）。
/// </summary>
public sealed class OcrService : IOcrService
{
    // 预处理尺寸阈值：过小图片等比放大，过大图片等比缩小，以平衡识别率与性能。
    private const int MinEdgeThreshold = 720;
    private const int MaxEdgeThreshold = 1920;

    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return OcrResult.Failure("图片内容为空");
        }

        try
        {
            // 预处理在线程池执行，避免阻塞 UI；await 会回到调用线程（UI）。
            var processedBytes = await Task.Run(() => PreprocessImage(imageBytes), cancellationToken);

            using var bitmap = await LoadSoftwareBitmapAsync(processedBytes);
            return await RecognizeBitmapAsync(bitmap, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OcrResult.Failure("OCR 识别失败: " + ex.Message);
        }
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return OcrResult.Failure("图片文件不存在");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            return await RecognizeAsync(bytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OcrResult.Failure("读取图片失败: " + ex.Message);
        }
    }

    public IReadOnlyList<string> GetAvailableLanguages()
    {
        try
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static byte[] PreprocessImage(byte[] bytes)
    {
        using var image = Image.Load(bytes);
        image.Mutate(x => x.AutoOrient());

        var minEdge = Math.Min(image.Width, image.Height);
        var maxEdge = Math.Max(image.Width, image.Height);

        if (minEdge < MinEdgeThreshold)
        {
            var scale = (double)MinEdgeThreshold / minEdge;
            var newWidth = (int)Math.Round(image.Width * scale);
            var newHeight = (int)Math.Round(image.Height * scale);
            image.Mutate(x => x.Resize(newWidth, newHeight));
        }
        else if (maxEdge > MaxEdgeThreshold)
        {
            var scale = (double)MaxEdgeThreshold / maxEdge;
            var newWidth = (int)Math.Round(image.Width * scale);
            var newHeight = (int)Math.Round(image.Height * scale);
            image.Mutate(x => x.Resize(newWidth, newHeight));
        }

        using var outputStream = new MemoryStream();
        image.Save(outputStream, new PngEncoder());
        return outputStream.ToArray();
    }

    private static async Task<SoftwareBitmap> LoadSoftwareBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        await stream.FlushAsync();

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }

    private static async Task<OcrResult> RecognizeBitmapAsync(SoftwareBitmap bitmap, CancellationToken cancellationToken)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return OcrResult.Failure("未找到可用的 OCR 引擎，请在 Windows 设置中安装 OCR 语言包。");
        }

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var text = result?.Text ?? string.Empty;

        return string.IsNullOrWhiteSpace(text)
            ? OcrResult.Failure("未识别到文字，请检查图片是否包含文本或是否已安装对应语言的 OCR 包。")
            : OcrResult.Success(text.Trim(), $"识别完成（{result.Lines.Count} 行）");
    }
}
