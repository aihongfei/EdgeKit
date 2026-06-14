namespace EdgeKit.Core.Settings;

/// <summary>
/// 设置的底层键值存储契约。由数据层（SQLite）实现，供 SettingsService 读写。
/// 与具体存储介质解耦，使 Services 层不直接依赖数据层。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取全部键值对。库不存在时返回空集合。</summary>
    IReadOnlyDictionary<string, string> LoadAll();

    /// <summary>写入或更新一个键值（内存暂存，待 <see cref="Save"/> 提交）。</summary>
    void Set(string key, string value);

    /// <summary>把暂存的键值持久化到底层存储。</summary>
    void Save();
}