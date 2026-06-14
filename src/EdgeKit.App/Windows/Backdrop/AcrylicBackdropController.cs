using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT;

namespace EdgeKit.App.Windows.Backdrop;

/// <summary>
/// 手动接管窗口的 Acrylic 毛玻璃背景。
/// State 固定为 Active，使窗口失去焦点时毛玻璃依然保持，而不是退化为纯色 Fallback。
/// unpackaged 场景需先确保系统 DispatcherQueue 存在，否则 backdrop 不渲染。
/// </summary>
internal sealed class AcrylicBackdropController
{
    private readonly WindowsSystemDispatcherQueueHelper _dispatcherQueueHelper = new();
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    /// <summary>把 Acrylic 背景附加到指定窗口。系统不支持时静默跳过。</summary>
    public void Attach(Window window)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        // unpackaged 场景必须先确保系统 DispatcherQueue 存在，否则 backdrop 不渲染。
        _dispatcherQueueHelper.EnsureWindowsSystemDispatcherQueueController();

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = Color.FromArgb(255, 32, 32, 36),
            LuminosityOpacity = 0.55f,
            TintOpacity = 0.35f,
            FallbackColor = Color.FromArgb(255, 32, 32, 36)
        };

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        _acrylicController.AddSystemBackdropTarget(
            window.As<ICompositionSupportsSystemBackdrop>());
    }

    public void Dispose()
    {
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
    }
}