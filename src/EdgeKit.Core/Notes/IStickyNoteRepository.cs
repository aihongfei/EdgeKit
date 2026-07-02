namespace EdgeKit.Core.Notes;

/// <summary>
/// 便签数据持久化接口。
/// </summary>
public interface IStickyNoteRepository
{
    /// <summary>数据变更事件（增删改）发生后触发。</summary>
    event EventHandler? Changed;

    /// <summary>获取所有便签，按创建时间倒序。</summary>
    IReadOnlyList<StickyNote> GetAll();

    /// <summary>按 Id 获取便签，不存在返回 null。</summary>
    StickyNote? GetById(long id);

    /// <summary>新增便签并返回自增 Id。</summary>
    long Add(StickyNote note);

    /// <summary>更新便签全部字段。</summary>
    void Update(StickyNote note);

    /// <summary>删除指定便签。</summary>
    void Delete(long id);
}