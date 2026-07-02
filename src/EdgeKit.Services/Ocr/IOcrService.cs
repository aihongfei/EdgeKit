namespace EdgeKit.Services.Ocr;

/// <summary>
/// OCR 文字识别服务契约。
/// </summary>
public interface IOcrService
{
    /// <summary>
    /// 识别图片字节中的文字。调用方应确保在 UI 线程调用（WinRT OCR 引擎要求 STA）。
    /// </summary>
    Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// 识别图片文件中的文字。
    /// </summary>
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前系统已安装的 OCR 语言标签列表（如 zh-Hans、en-US）。
    /// </summary>
    IReadOnlyList<string> GetAvailableLanguages();
}

/// <summary>
/// OCR 识别结果。
/// </summary>
public sealed record OcrResult(bool IsSuccess, string Text, string Message)
{
    /// <summary>构造成功结果。</summary>
    public static OcrResult Success(string text, string message)
        => new(true, text, message);

    /// <summary>构造失败结果。</summary>
    public static OcrResult Failure(string message)
        => new(false, string.Empty, message);
}