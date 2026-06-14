namespace EdgeKit.Core.Commands;

/// <summary>用户自定义命令仓储。</summary>
public interface ICustomCommandRepository
{
    /// <summary>自定义命令发生变更。</summary>
    event EventHandler? Changed;

    /// <summary>获取全部自定义命令。</summary>
    IReadOnlyList<CustomCommand> GetAll();

    /// <summary>新增自定义命令，返回新 Id。</summary>
    long Add(CustomCommand command);

    /// <summary>删除自定义命令。</summary>
    void Delete(long id);
}
