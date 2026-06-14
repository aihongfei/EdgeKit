namespace EdgeKit.Core.Commands;

/// <summary>命令面板与统一搜索使用的命令描述。</summary>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Description,
    string Glyph,
    string Category,
    CommandKind Kind,
    string Target,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyList<string> Keywords,
    int Order,
    bool RequiresConfirmation = false,
    bool RunAsAdministrator = false,
    bool IsCustom = false,
    long? CustomCommandId = null);
