namespace EdgeKit.Core.Services;

/// <summary>
/// 抽屉/窗口的显隐、置顶、固定等控制能力。
/// 本轮仅定义接口，具体实现后续在 App / Services 层补全。
/// </summary>
public interface IWindowService
{
    /// <summary>显示抽屉窗口。</summary>
    void ShowDrawer();

    /// <summary>隐藏抽屉窗口。</summary>
    void HideDrawer();

    /// <summary>切换抽屉的固定状态（固定后不自动收起）。</summary>
    void SetPinned(bool pinned);

    /// <summary>当前是否已固定。</summary>
    bool IsPinned { get; }
}