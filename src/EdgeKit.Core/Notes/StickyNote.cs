namespace EdgeKit.Core.Notes;

/// <summary>
/// 桌面便签领域模型。
/// </summary>
public sealed record StickyNote(
    long Id,
    string Content,
    string ColorHex,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsTopmost,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);