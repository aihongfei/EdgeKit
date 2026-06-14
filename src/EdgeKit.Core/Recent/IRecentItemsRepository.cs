namespace EdgeKit.Core.Recent;

using System;
using System.Collections.Generic;

/// <summary>
/// 最近使用项的仓储契约。记录最近使用的工具与最近活动的窗口，支持读取、写入与清除。
/// 底层由数据层（SQLite）实现，与具体存储介质解耦。
/// </summary>
public interface IRecentItemsRepository
{
    /// <summary>
    /// 记录发生变更（写入或清除）后触发，供 UI 实时刷新。
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// 写入或更新一条记录：若同 <see cref="RecentItemKind"/> + Key 已存在则更新标题/时间并累加使用次数，
    /// 否则插入新记录。写入后按类型裁剪，仅保留最近若干条。
    /// </summary>
    void Touch(RecentItem item);

    /// <summary>
    /// 按类型读取最近记录，按最后使用时间倒序，最多 <paramref name="limit"/> 条。
    /// </summary>
    IReadOnlyList<RecentItem> GetRecent(RecentItemKind kind, int limit);

    /// <summary>
    /// 清除记录。<paramref name="kind"/> 为 null 时清除全部类型。
    /// </summary>
    void Clear(RecentItemKind? kind);
}