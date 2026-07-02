using EdgeKit.Core.Clipboard;
using EdgeKit.Services.Ocr;

namespace EdgeKit.App.Views;

public sealed record ImageOcrPageParameter(
    IOcrService OcrService,
    IClipboardRepository ClipboardRepository,
    nint Hwnd);