using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.Search;

/// <summary>
/// 本机已安装应用条目（来自 shell:AppsFolder，涵盖 Win32 与 UWP/商店应用）。
/// <see cref="AppUserModelId"/> 是启动用的解析名（DESKTOPABSOLUTEPARSING），
/// 用 <c>explorer.exe shell:AppsFolder\{id}</c> 即可拉起。
/// </summary>
public sealed class InstalledApp
{
    public InstalledApp(
        string displayName,
        string appUserModelId,
        BitmapImage? iconImage,
        string locationPath = "",
        string executablePath = "")
    {
        DisplayName = displayName;
        AppUserModelId = appUserModelId;
        IconImage = iconImage;
        LocationPath = locationPath;
        ExecutablePath = executablePath;
    }

    /// <summary>显示名称（如“微信”“Visual Studio Code”）。</summary>
    public string DisplayName { get; }

    /// <summary>启动用的解析名 / AppUserModelID。</summary>
    public string AppUserModelId { get; }

    /// <summary>打开文件位置优先使用的真实文件系统路径。</summary>
    public string LocationPath { get; set; }

    /// <summary>快捷方式解析出的真实可执行文件路径。</summary>
    public string ExecutablePath { get; set; }

    /// <summary>应用图标；取不到时为 null，UI 降级为首字母占位。</summary>
    public BitmapImage? IconImage { get; set; }
}