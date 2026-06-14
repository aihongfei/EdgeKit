namespace EdgeKit.Core.Services;

/// <summary>管理当前用户的 EdgeKit 开机自启项。</summary>
public interface IStartupLaunchService
{
    StartupLaunchStatus GetStatus();

    StartupLaunchResult SetEnabled(bool enabled);

    void RefreshCommandIfEnabled();
}

public sealed record StartupLaunchStatus(bool IsEnabled, string? Command);

public sealed record StartupLaunchResult(bool Succeeded, string? ErrorMessage);
