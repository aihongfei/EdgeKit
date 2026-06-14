namespace EdgeKit.Core.Commands;

/// <summary>命令的执行来源类型。</summary>
public enum CommandKind
{
    /// <summary>EdgeKit 内置动作。</summary>
    BuiltInAction,

    /// <summary>Windows URI / Shell URI，例如 ms-settings:display 或 shell:Downloads。</summary>
    WindowsUri,

    /// <summary>文件或文件夹路径。</summary>
    Path,

    /// <summary>进程命令行。</summary>
    Process,

    /// <summary>用户自定义命令。</summary>
    Custom
}
