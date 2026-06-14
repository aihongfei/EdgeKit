using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using EdgeKit.App.Interaction;
using EdgeKit.Core.Clipboard;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 剪贴板历史页视图模型。负责拉取历史与分组、按分组筛选、复制/删除/置顶/切换分组，
/// 以及分组的增删改。订阅仓储 <see cref="IClipboardRepository.Changed"/> 实时刷新。
/// </summary>
public sealed class ClipboardHistoryViewModel
{
    // 列表展示条数上限。
    private const int DisplayLimit = 300;

    private readonly IClipboardRepository _repository;
    private readonly ClipboardContentWriter _clipboardWriter;

    public ClipboardHistoryViewModel(IClipboardRepository repository, ClipboardContentWriter clipboardWriter)
    {
        _repository = repository;
        _clipboardWriter = clipboardWriter;
    }

    /// <summary>历史条目（当前筛选下）。</summary>
    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    /// <summary>全部分组。</summary>
    public ObservableCollection<ClipboardGroup> Groups { get; } = new();

    /// <summary>当前筛选的分组 Id；null 表示“全部”。</summary>
    public long? SelectedGroupId { get; private set; }

    /// <summary>当前搜索关键字；非空时搜索所有分组。</summary>
    public string SearchKeyword { get; private set; } = string.Empty;

    /// <summary>仓储变更事件，供页面在可见期间订阅。</summary>
    public event EventHandler? RepositoryChanged
    {
        add => _repository.Changed += value;
        remove => _repository.Changed -= value;
    }

    /// <summary>是否当前无任何历史条目（空状态绑定）。</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>切换筛选分组并刷新。</summary>
    public void SetGroupFilter(long? groupId)
    {
        SelectedGroupId = groupId;
        Refresh();
    }

    /// <summary>设置搜索关键字。非空搜索会忽略分组筛选，在全部历史中查找。</summary>
    public void SetSearchKeyword(string? keyword)
    {
        SearchKeyword = keyword?.Trim() ?? string.Empty;
        Refresh();
    }

    /// <summary>重新拉取分组与条目。须在 UI 线程调用（构造 BitmapImage）。</summary>
    public void Refresh()
    {
        Groups.Clear();
        foreach (var group in _repository.GetGroups())
        {
            Groups.Add(group);
        }

        Items.Clear();
        var groupId = string.IsNullOrWhiteSpace(SearchKeyword) ? SelectedGroupId : null;
        var keyword = string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword;

        foreach (var item in _repository.Get(groupId, kind: null, keyword, DisplayLimit))
        {
            Items.Add(new ClipboardItemViewModel(item));
        }
    }

    /// <summary>把条目内容写回系统剪贴板（设跳过标记避免重复记录）。</summary>
    public void Copy(ClipboardItemViewModel vm)
    {
        _clipboardWriter.Copy(vm.Model);
    }

    /// <summary>删除条目。</summary>
    public void Delete(ClipboardItemViewModel vm) => _repository.Delete(vm.Id);

    /// <summary>切换固定状态。</summary>
    public void TogglePin(ClipboardItemViewModel vm) => _repository.SetPinned(vm.Id, !vm.Pinned);

    /// <summary>把条目移动到指定分组；null 表示移出分组。</summary>
    public void MoveToGroup(ClipboardItemViewModel vm, long? groupId)
        => _repository.MoveToGroup(vm.Id, groupId);

    /// <summary>新建分组，返回新分组 Id。</summary>
    public long AddGroup(string name, string colorHex) => _repository.AddGroup(name, colorHex);

    /// <summary>重命名分组。</summary>
    public void RenameGroup(long id, string name) => _repository.RenameGroup(id, name);

    /// <summary>删除分组。</summary>
    public void DeleteGroup(long id)
    {
        _repository.DeleteGroup(id);
        if (SelectedGroupId == id)
        {
            SelectedGroupId = null;
        }
    }

    /// <summary>清空当前筛选分组（或全部）的历史。</summary>
    public void ClearCurrent() => _repository.Clear(SelectedGroupId);

    /// <summary>用于右键菜单构建“切换分组”子项的分组快照。</summary>
    public IReadOnlyList<ClipboardGroup> GetGroupsSnapshot() => _repository.GetGroups();
}
