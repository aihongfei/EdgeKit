using System;
using System.Collections.Generic;

namespace EdgeKit.Core.QuickLaunch;

/// <summary>
/// 首页快速启动项仓储契约。快速启动项按添加顺序展示，重复固定同一目标时更新标题不重复插入。
/// </summary>
public interface IQuickLaunchRepository
{
    /// <summary>快速启动项发生新增、更新或删除时触发。</summary>
    event EventHandler? Changed;

    /// <summary>读取全部快速启动项，按 SortOrder 升序。</summary>
    IReadOnlyList<QuickLaunchItem> GetAll();

    /// <summary>新增或更新一条快速启动项，返回最终记录 Id。</summary>
    long AddOrUpdate(QuickLaunchItem item);

    /// <summary>删除指定 Id 的快速启动项。</summary>
    void Delete(long id);

    /// <summary>按类型与目标删除快速启动项。</summary>
    void DeleteByTarget(QuickLaunchItemKind kind, string target);

    /// <summary>判断目标是否已固定。</summary>
    bool Contains(QuickLaunchItemKind kind, string target);
}
