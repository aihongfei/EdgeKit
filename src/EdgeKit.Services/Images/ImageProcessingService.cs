using System.Globalization;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace EdgeKit.Services.Images;

public sealed class ImageProcessingService
{
    private const long MaxDecodedPixelBytes = 256L * 1024 * 1024;

    public Task<ImageInspectionResult> InspectAsync(byte[] bytes, CancellationToken cancellationToken = default)
        => Task.Run(() => InspectCore(bytes, cancellationToken), cancellationToken);

    public Task<ImageConversionResult> ConvertAsync(
        byte[] bytes,
        ImageOutputFormat outputFormat,
        int quality,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ConvertCore(bytes, outputFormat, quality, cancellationToken), cancellationToken);

    private static ImageInspectionResult InspectCore(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bytes is null || bytes.Length == 0)
        {
            return ImageInspectionResult.Failure("图片内容为空");
        }

        try
        {
            var imageFormat = Image.DetectFormat(bytes);
            var imageInfo = Image.Identify(bytes);
            var descriptor = BuildDescriptor(imageFormat, imageInfo, bytes.LongLength);

            if (descriptor.PixelMemoryBytes > MaxDecodedPixelBytes)
            {
                return ImageInspectionResult.Failure("图片过大，超出可处理范围");
            }

            var message = descriptor.IsAnimated
                ? $"已读取 {descriptor.FormatName}，检测到动画帧，转换时将使用首帧"
                : $"已读取 {descriptor.FormatName}";
            return ImageInspectionResult.Success(descriptor, message);
        }
        catch (Exception ex) when (IsImageException(ex))
        {
            return ImageInspectionResult.Failure("无法识别图片: " + ex.Message);
        }
    }

    private static ImageConversionResult ConvertCore(
        byte[] bytes,
        ImageOutputFormat outputFormat,
        int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var inspection = InspectCore(bytes, cancellationToken);
        if (!inspection.IsSuccess || inspection.Descriptor is null)
        {
            return ImageConversionResult.Failure(inspection.Message);
        }

        try
        {
            using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, bytes);
            image.Mutate(x => x.AutoOrient());
            if (outputFormat == ImageOutputFormat.Jpeg)
            {
                image.Mutate(x => x.BackgroundColor(Color.White));
            }

            using var outputStream = new MemoryStream();
            image.Save(outputStream, CreateEncoder(outputFormat, quality));
            var outputBytes = outputStream.ToArray();

            var outputFormatInfo = Image.DetectFormat(outputBytes);
            var outputInfo = Image.Identify(outputBytes);
            if (EstimatePixelMemoryBytes(outputInfo) > MaxDecodedPixelBytes)
            {
                return ImageConversionResult.Failure("输出图片过大，超出可处理范围");
            }

            var outputDescriptor = BuildDescriptor(outputFormatInfo, outputInfo, outputBytes.LongLength);
            var message = BuildConversionMessage(inspection.Descriptor, outputDescriptor, outputFormat);
            return ImageConversionResult.Success(outputBytes, inspection.Descriptor, outputDescriptor, message);
        }
        catch (Exception ex) when (IsImageException(ex))
        {
            return ImageConversionResult.Failure("图片转换失败: " + ex.Message);
        }
    }

    private static IImageEncoder CreateEncoder(ImageOutputFormat outputFormat, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);

        return outputFormat switch
        {
            ImageOutputFormat.Png => new PngEncoder
            {
                CompressionLevel = quality >= 60
                    ? PngCompressionLevel.BestCompression
                    : PngCompressionLevel.DefaultCompression
            },
            ImageOutputFormat.Jpeg => new JpegEncoder
            {
                Quality = quality
            },
            ImageOutputFormat.Webp => new WebpEncoder
            {
                FileFormat = WebpFileFormatType.Lossy,
                Quality = quality
            },
            ImageOutputFormat.Bmp => new BmpEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(outputFormat), outputFormat, null)
        };
    }

    private static ImageDescriptor BuildDescriptor(IImageFormat? format, ImageInfo info, long fileSizeBytes)
    {
        var formatName = format?.Name ?? info.Metadata.DecodedImageFormat?.Name ?? "未知格式";
        var mimeType = format?.DefaultMimeType ?? info.Metadata.DecodedImageFormat?.DefaultMimeType ?? "application/octet-stream";
        var extension = format?.FileExtensions?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".img";
        }
        else if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return new ImageDescriptor(
            formatName,
            mimeType,
            extension,
            info.Width,
            info.Height,
            GetFrameCount(info),
            fileSizeBytes,
            EstimatePixelMemoryBytes(info),
            GetFrameCount(info) > 1);
    }

    private static string BuildConversionMessage(
        ImageDescriptor source,
        ImageDescriptor output,
        ImageOutputFormat outputFormat)
    {
        var animatedNote = source.IsAnimated
            ? "，动画已按首帧处理"
            : string.Empty;

        return $"{source.FormatName} → {outputFormat.ToDisplayName()} 完成（{output.DimensionsText}，{output.FileSizeText}{animatedNote}）";
    }

    private static bool IsImageException(Exception exception)
        => exception is InvalidImageContentException
            or UnknownImageFormatException
            or ImageFormatException
            or NotSupportedException
            or ArgumentException
            or IOException;

    private static int GetFrameCount(ImageInfo info)
        => info.FrameMetadataCollection.Count;

    private static long EstimatePixelMemoryBytes(ImageInfo info)
    {
        var pixels = (long)info.Width * info.Height;
        var bytesPerPixel = Math.Max(1, info.PixelType.BitsPerPixel) / 8d;
        var frames = Math.Max(1, GetFrameCount(info));
        return (long)Math.Ceiling(pixels * bytesPerPixel * frames);
    }
}

public enum ImageOutputFormat
{
    Png,
    Jpeg,
    Webp,
    Bmp
}

public sealed record ImageDescriptor(
    string FormatName,
    string MimeType,
    string Extension,
    int Width,
    int Height,
    int FrameCount,
    long FileSizeBytes,
    long PixelMemoryBytes,
    bool IsAnimated)
{
    public string DimensionsText => $"{Width} × {Height}";

    public string FileSizeText => FormatByteSize(FileSizeBytes);

    public string PixelMemoryText => FormatByteSize(PixelMemoryBytes);

    public string FrameText => FrameCount > 1 ? $"{FrameCount} 帧" : "单帧";

    public string SummaryText => $"{FormatName} · {DimensionsText} · {FileSizeText}";

    private static string FormatByteSize(long byteCount)
    {
        if (byteCount < 1024)
        {
            return byteCount.ToString(CultureInfo.InvariantCulture) + " B";
        }

        var kib = byteCount / 1024d;
        if (kib < 1024)
        {
            return kib.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (kib / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    }
}

public sealed record ImageInspectionResult(bool IsSuccess, ImageDescriptor? Descriptor, string Message)
{
    public static ImageInspectionResult Success(ImageDescriptor descriptor, string message)
        => new(true, descriptor, message);

    public static ImageInspectionResult Failure(string message)
        => new(false, null, message);
}

public sealed record ImageConversionResult(
    bool IsSuccess,
    byte[] OutputBytes,
    ImageDescriptor? Source,
    ImageDescriptor? Output,
    string Message)
{
    public static ImageConversionResult Success(byte[] outputBytes, ImageDescriptor source, ImageDescriptor output, string message)
        => new(true, outputBytes, source, output, message);

    public static ImageConversionResult Failure(string message)
        => new(false, Array.Empty<byte>(), null, null, message);
}

internal static class ImageOutputFormatExtensions
{
    public static string ToDisplayName(this ImageOutputFormat format)
        => format switch
        {
            ImageOutputFormat.Png => "PNG",
            ImageOutputFormat.Jpeg => "JPG",
            ImageOutputFormat.Webp => "WebP",
            ImageOutputFormat.Bmp => "BMP",
            _ => format.ToString()
        };
}
