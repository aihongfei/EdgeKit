using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeKit.Core.Clipboard;

/// <summary>
/// 剪贴板自动分组的预留入口（后续可接入大模型）。
/// 捕获新条目后由服务层调用，返回应归属的分组 Id；返回 null 表示不分组。
/// 当前默认实现 <c>NoopClipboardClassifier</c> 始终返回 null，
/// 接入大模型时只需替换该接口的注册实现，无需改动捕获链路与 UI。
/// </summary>
public interface IClipboardClassifier
{
    /// <summary>
    /// 根据条目内容与现有分组列表，建议一个归属分组 Id。
    /// </summary>
    /// <param name="item">待分类的剪贴板条目。</param>
    /// <param name="groups">当前已有的分组列表，供模型选择。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>建议的分组 Id；无合适分组或不分组时返回 null。</returns>
    Task<long?> SuggestGroupAsync(
        ClipboardItem item,
        IReadOnlyList<ClipboardGroup> groups,
        CancellationToken cancellationToken = default);
}