using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.App.Interaction;
using EdgeKit.Core.Agent;
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

    private static readonly string[] GroupColors =
    [
        "#4C8DFF",
        "#00D4AA",
        "#FF4D94",
        "#FFAA00",
        "#9B59B6",
        "#E74C3C"
    ];

    private readonly IClipboardRepository _repository;
    private readonly ClipboardContentWriter _clipboardWriter;
    private readonly IAgentService _agentService;

    public ClipboardHistoryViewModel(IClipboardRepository repository, ClipboardContentWriter clipboardWriter, IAgentService agentService)
    {
        _repository = repository;
        _clipboardWriter = clipboardWriter;
        _agentService = agentService;
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

    /// <summary>自动分组状态变更事件。</summary>
    public event EventHandler? AutoGroupingChanged;

    private bool _isAutoGrouping;

    /// <summary>是否正在执行自动分组。</summary>
    public bool IsAutoGrouping
    {
        get => _isAutoGrouping;
        private set
        {
            if (_isAutoGrouping != value)
            {
                _isAutoGrouping = value;
                AutoGroupingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
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

        foreach (var item in _repository.Get(groupId, kind: null, keyword, DisplayLimit, null, null))
        {
            Items.Add(new ClipboardItemViewModel(item));
        }
    }

    /// <summary>调用智能体按语义自动分组指定时间范围内的剪贴板记录。</summary>
    public async Task AutoGroupAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        IsAutoGrouping = true;
        try
        {
            var items = _repository.Get(null, null, null, int.MaxValue, startUtc, endUtc);
            if (items.Count == 0)
            {
                return;
            }

            var prompt = BuildAutoGroupPrompt(items);
            var conversation = _agentService.CreateConversation(AgentConversationMode.Chat);

            try
            {
                var result = await _agentService.SendAsync(conversation.Id, prompt, cancellationToken).ConfigureAwait(false);
                if (!result.Success || string.IsNullOrWhiteSpace(result.AssistantMessage?.Content))
                {
                    return;
                }

                ApplyAutoGroupResult(result.AssistantMessage.Content, items);
            }
            finally
            {
                _agentService.DeleteConversation(conversation.Id);
            }
        }
        finally
        {
            IsAutoGrouping = false;
        }
    }

    private static string BuildAutoGroupPrompt(IReadOnlyList<ClipboardItem> items)
    {
        var lines = items.Select(item =>
        {
            var kind = item.Kind.ToString();
            var preview = string.IsNullOrWhiteSpace(item.Preview) ? item.Content : item.Preview;
            preview = preview.Replace('\n', ' ').Replace('\r', ' ');
            if (preview.Length > 200)
            {
                preview = preview[..200];
            }

            return $"[{item.Id}] {kind}: {preview}";
        });

        return
            "请根据以下剪贴板记录的内容语义进行自动分组。\n" +
            "要求：\n" +
            "1. 同一分组的记录语义相近；\n" +
            "2. 如果一个记录无法归入任何有意义的组，可以忽略；\n" +
            "3. 返回严格的 JSON 数组格式，不要附加解释；\n" +
            "4. 颜色从以下列表中选择：#4C8DFF、#00D4AA、#FF4D94、#FFAA00、#9B59B6、#E74C3C。\n\n" +
            "JSON 格式示例：\n" +
            "{\n" +
            "  \"groups\": [\n" +
            "    {\"name\": \"工作代码\", \"colorHex\": \"#4C8DFF\", \"itemIds\": [1, 2, 3]},\n" +
            "    {\"name\": \"常用链接\", \"colorHex\": \"#00D4AA\", \"itemIds\": [4, 5]}\n" +
            "  ]\n" +
            "}\n\n" +
            "记录列表（[Id] 类型: 预览）：\n" +
            string.Join("\n", lines);
    }

    private void ApplyAutoGroupResult(string assistantContent, IReadOnlyList<ClipboardItem> sourceItems)
    {
        var json = ExtractJson(assistantContent);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var existingGroups = _repository.GetGroups().ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var itemIdSet = sourceItems.Select(i => i.Id).ToHashSet();

        foreach (var groupElement in groupsElement.EnumerateArray())
        {
            var name = groupElement.GetProperty("name").GetString();
            var colorHex = groupElement.GetProperty("colorHex").GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(colorHex))
            {
                continue;
            }

            var itemIds = groupElement.GetProperty("itemIds")
                .EnumerateArray()
                .Select(e => e.GetInt64())
                .Where(id => itemIdSet.Contains(id))
                .ToArray();

            if (itemIds.Length == 0)
            {
                continue;
            }

            long groupId;
            if (!existingGroups.TryGetValue(name, out var existingGroup))
            {
                groupId = _repository.AddGroup(name.Trim(), colorHex);
            }
            else
            {
                groupId = existingGroup.Id;
            }

            foreach (var id in itemIds)
            {
                _repository.MoveToGroup(id, groupId);
            }
        }
    }

    private static string? ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return content[start..(end + 1)];
        }

        return null;
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
