using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.Core.Clipboard;

namespace EdgeKit.Services.Clipboard;

/// <summary>
/// 默认的剪贴板分类器：不做任何自动分组，始终返回 null。
/// 作为大模型自动分组接入前的占位实现，接入时替换为调用模型的实现并改 DI 注册即可。
/// </summary>
public sealed class NoopClipboardClassifier : IClipboardClassifier
{
    public Task<long?> SuggestGroupAsync(
        ClipboardItem item,
        IReadOnlyList<ClipboardGroup> groups,
        CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(null);
}