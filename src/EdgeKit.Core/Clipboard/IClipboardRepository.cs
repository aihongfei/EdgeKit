using System;
using System.Collections.Generic;

namespace EdgeKit.Core.Clipboard;

/// <summary>
/// 剪贴板历史与分组的仓储契约。底层由数据层（SQLite）实现，与存储介质解耦。
/// 任意写操作（新增/删除/清空/置顶/分组变更）后触发 <see cref="Changed"/> 供 UI 实时刷新。
/// </summary>
public interface IClipboardRepository
{
    /// <summary>记录或分组发生变更后触发。</summary>
    event EventHandler? Changed;

    // ---- 条目 ----

    /// <summary>
    /// 新增一条记录。若 <see cref="ClipboardItem.Hash"/> 已存在则视为重复：
    /// 更新其时间置顶、不新增。返回最终入库记录的 Id。
    /// </summary>
    long Add(ClipboardItem item);

    /// <summary>删除一条记录（连带其图片文件，由实现处理）。</summary>
    void Delete(long id);

    /// <summary>
    /// 清空记录。<paramref name="groupId"/> 为 null 时清空全部；否则仅清空该分组下记录。
    /// </summary>
    void Clear(long? groupId);

    /// <summary>设置某条记录的固定状态。</summary>
    void SetPinned(long id, bool pinned);

    /// <summary>把某条记录移动到指定分组；<paramref name="groupId"/> 为 null 表示移出分组。</summary>
    void MoveToGroup(long id, long? groupId);

    /// <summary>
    /// 查询记录。可选按分组、类型、关键字、UTC 时间范围筛选，按固定优先 + 时间倒序，最多 <paramref name="limit"/> 条。
    /// <paramref name="groupId"/> 为 null 表示不限分组（全部）。
    /// </summary>
    IReadOnlyList<ClipboardItem> Get(long? groupId, ClipboardItemKind? kind, string? keyword, int limit, DateTime? startUtc = null, DateTime? endUtc = null);

    // ---- 分组 ----

    /// <summary>读取全部分组，按 SortOrder 升序。</summary>
    IReadOnlyList<ClipboardGroup> GetGroups();

    /// <summary>新增分组，返回新分组 Id。</summary>
    long AddGroup(string name, string colorHex);

    /// <summary>重命名分组。</summary>
    void RenameGroup(long id, string name);

    /// <summary>删除分组（该组下记录的 GroupId 置空，记录本身保留）。</summary>
    void DeleteGroup(long id);
}