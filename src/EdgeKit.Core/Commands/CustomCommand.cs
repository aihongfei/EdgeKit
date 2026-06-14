namespace EdgeKit.Core.Commands;

/// <summary>用户自定义命令，持久化到 SQLite。</summary>
public sealed record CustomCommand(
    long Id,
    string Title,
    string CommandText,
    string Arguments,
    string WorkingDirectory,
    string Glyph,
    string Keywords,
    bool RunAsAdministrator,
    bool RequiresConfirmation,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
