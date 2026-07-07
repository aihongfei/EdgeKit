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
        => Attach(window, Color.FromArgb(255, 32, 32, 36), 0.55f, 0.55f);

    /// <summary>
    /// 附加 Acrylic 背景并指定染色与不透明度。浮层类窗口可用更轻的染色获得更通透的毛玻璃观感。
    /// </summary>
    public void Attach(Window window, Color tint, float luminosityOpacity, float tintOpacity)
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
            TintColor = tint,
            LuminosityOpacity = luminosityOpacity,
            TintOpacity = tintOpacity,
            FallbackColor = tint
        };

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        _acrylicController.AddSystemBackdropTarget(
            window.As<ICompositionSupportsSystemBackdrop>());
    }

    /// <summary>
    /// 动态修改 Acrylic 染色色。便签窗口用此把便签颜色融合到半透明背景上。
    /// </summary>
    public void SetTintColor(Color color)
    {
        if (_acrylicController is null)
        {
            return;
        }

        _acrylicController.TintColor = color;
        _acrylicController.FallbackColor = color;
    }

    public void Dispose()
    {
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
    }
}