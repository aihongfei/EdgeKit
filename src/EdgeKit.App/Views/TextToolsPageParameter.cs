using EdgeKit.Services.Text;

namespace EdgeKit.App.Views;

public sealed record TextToolsPageParameter(string ToolId, TextProcessingService TextTools, nint Hwnd);
