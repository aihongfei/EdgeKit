using EdgeKit.Services.Images;

namespace EdgeKit.App.Views;

public sealed record ImageConvertPageParameter(ImageProcessingService ImageTools, nint Hwnd);
