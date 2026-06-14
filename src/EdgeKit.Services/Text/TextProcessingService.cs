using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EdgeKit.Services.Text;

public sealed class TextProcessingService
{
    private const string DataImagePrefix = "data:image/";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string UrlEncode(string input) => WebUtility.UrlEncode(input);

    public string UrlDecode(string input) => WebUtility.UrlDecode(input);

    public string Base64Encode(string input)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

    public TextToolResult Base64Decode(string input)
    {
        try
        {
            return TextToolResult.Success(Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(input))));
        }
        catch (FormatException ex)
        {
            return TextToolResult.Failure("Base64 内容无效: " + ex.Message);
        }
    }

    public ImageBase64Result ImageToBase64(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return ImageBase64Result.Failure("图片内容为空");
        }

        var format = DetectImageFormat(bytes);
        if (format is null)
        {
            return ImageBase64Result.Failure("不支持的图片格式，请使用 PNG、JPG、WEBP、GIF 或 BMP");
        }

        var output = $"data:{format.MimeType};base64,{Convert.ToBase64String(bytes)}";
        return ImageBase64Result.Success(
            output,
            bytes,
            format.MimeType,
            format.Extension,
            $"图片已转 Base64（{format.MimeType}，{FormatByteSize(bytes.Length)}）");
    }

    public ImageBase64Result Base64ToImage(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ImageBase64Result.Failure("请输入图片 Base64 内容");
        }

        string? mimeType = null;
        if (value.StartsWith(DataImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = value.IndexOf(',');
            if (commaIndex < 0)
            {
                return ImageBase64Result.Failure("Data URL 格式无效，缺少 Base64 内容");
            }

            var header = value[..commaIndex];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return ImageBase64Result.Failure("Data URL 不是 Base64 图片");
            }

            mimeType = header[5..].Split(';', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            value = value[(commaIndex + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(NormalizeBase64(value));
        }
        catch (FormatException ex)
        {
            return ImageBase64Result.Failure("图片 Base64 内容无效: " + ex.Message);
        }

        var format = DetectImageFormat(bytes);
        if (format is null)
        {
            return ImageBase64Result.Failure("Base64 内容不是支持的图片格式");
        }

        if (mimeType is not null && !mimeType.Equals(format.MimeType, StringComparison.OrdinalIgnoreCase))
        {
            mimeType = format.MimeType;
        }

        return ImageBase64Result.Success(
            string.Empty,
            bytes,
            mimeType ?? format.MimeType,
            format.Extension,
            $"图片解析完成（{mimeType ?? format.MimeType}，{FormatByteSize(bytes.Length)}）");
    }

    public TextToolResult FormatJson(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            return TextToolResult.Success(JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions), "JSON 格式化完成");
        }
        catch (JsonException ex)
        {
            return TextToolResult.Failure(BuildJsonErrorMessage(ex), ToJsonError(ex));
        }
    }

    public TextToolResult CompactJson(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            return TextToolResult.Success(JsonSerializer.Serialize(document.RootElement, CompactJsonOptions), "JSON 压缩完成");
        }
        catch (JsonException ex)
        {
            return TextToolResult.Failure(BuildJsonErrorMessage(ex), ToJsonError(ex));
        }
    }

    public TextToolResult ValidateJson(string input)
    {
        try
        {
            using var _ = JsonDocument.Parse(input);
            return TextToolResult.Success(input, "JSON 有效");
        }
        catch (JsonException ex)
        {
            return TextToolResult.Failure(BuildJsonErrorMessage(ex), ToJsonError(ex));
        }
    }

    public string EscapeJsonString(string input)
        => JsonSerializer.Serialize(input, CompactJsonOptions);

    public TextToolResult UnescapeJsonString(string input)
    {
        try
        {
            return TextToolResult.Success(JsonSerializer.Deserialize<string>(input) ?? string.Empty);
        }
        catch (JsonException ex)
        {
            return TextToolResult.Failure(BuildJsonErrorMessage(ex), ToJsonError(ex));
        }
    }

    private static string BuildJsonErrorMessage(JsonException ex)
    {
        var error = ToJsonError(ex);
        var location = error is null ? string.Empty : $"（第 {error.Line} 行，第 {error.Column} 列）";
        return "JSON 无效" + location + ": " + ex.Message;
    }

    private static JsonError? ToJsonError(JsonException ex)
    {
        if (ex.LineNumber is null || ex.BytePositionInLine is null)
        {
            return null;
        }

        return new JsonError(
            (int)ex.LineNumber.Value + 1,
            (int)ex.BytePositionInLine.Value + 1);
    }

    private static string NormalizeBase64(string input)
    {
        var value = input.Trim();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static ImageFormat? DetectImageFormat(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return new ImageFormat("image/png", ".png");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return new ImageFormat("image/jpeg", ".jpg");
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x38
            && (bytes[4] == 0x37 || bytes[4] == 0x39)
            && bytes[5] == 0x61)
        {
            return new ImageFormat("image/gif", ".gif");
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x46
            && bytes[8] == 0x57
            && bytes[9] == 0x45
            && bytes[10] == 0x42
            && bytes[11] == 0x50)
        {
            return new ImageFormat("image/webp", ".webp");
        }

        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return new ImageFormat("image/bmp", ".bmp");
        }

        return null;
    }

    private static string FormatByteSize(int byteCount)
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

    private sealed record ImageFormat(string MimeType, string Extension);
}

public sealed record JsonError(int Line, int Column)
{
    public string LineText => Line.ToString(CultureInfo.InvariantCulture);

    public string ColumnText => Column.ToString(CultureInfo.InvariantCulture);
}

public sealed record TextToolResult(bool IsSuccess, string Output, string Message, JsonError? JsonError = null)
{
    public static TextToolResult Success(string output, string message = "已完成")
        => new(true, output, message);

    public static TextToolResult Failure(string message, JsonError? jsonError = null)
        => new(false, string.Empty, message, jsonError);
}

public sealed record ImageBase64Result(
    bool IsSuccess,
    string Output,
    byte[] Bytes,
    string MimeType,
    string Extension,
    string Message)
{
    public static ImageBase64Result Success(string output, byte[] bytes, string mimeType, string extension, string message)
        => new(true, output, bytes, mimeType, extension, message);

    public static ImageBase64Result Failure(string message)
        => new(false, string.Empty, Array.Empty<byte>(), string.Empty, string.Empty, message);
}
