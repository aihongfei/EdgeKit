using System.Collections.ObjectModel;
using EdgeKit.App.Notes;
using EdgeKit.Core.Notes;

namespace EdgeKit.App.ViewModels;

/// <summary>
/// 便签管理页视图模型。
/// </summary>
public sealed class StickyNotesViewModel
{
    private readonly StickyNoteManagerService _manager;

    public StickyNotesViewModel(StickyNoteManagerService manager)
    {
        _manager = manager;
    }

    /// <summary>当前展示的管理项集合。</summary>
    public ObservableCollection<StickyNoteItemViewModel> Items { get; } = new();

    /// <summary>底层数据变更事件。</summary>
    public event EventHandler? NotesChanged
    {
        add => _manager.NotesChanged += value;
        remove => _manager.NotesChanged -= value;
    }

    /// <summary>重新加载便签列表。</summary>
    public void Refresh()
    {
        Items.Clear();
        foreach (var note in _manager.GetAllNotes())
        {
            Items.Add(new StickyNoteItemViewModel(note, _manager, this));
        }
    }

    /// <summary>新建一条便签并打开窗口。
    /// </summary>
    public void CreateNew()
    {
        _manager.CreateNew();
        Refresh();
    }

    internal void RefreshAfterAction()
    {
        Refresh();
    }
}

/// <summary>
/// 便签管理页单条展示项。
/// </summary>
public sealed class StickyNoteItemViewModel
{
    private readonly StickyNoteManagerService _manager;
    private readonly StickyNotesViewModel _parent;

    public StickyNoteItemViewModel(
        StickyNote note,
        StickyNoteManagerService manager,
        StickyNotesViewModel parent)
    {
        Note = note;
        _manager = manager;
        _parent = parent;
    }

    public StickyNote Note { get; private set; }

    /// <summary>列表中显示的内容预览。</summary>
    public string PreviewText => string.IsNullOrWhiteSpace(Note.Content) ? "（空白便签）" : Note.Content;

    /// <summary>该便签当前是否已打开独立窗口。</summary>
    public bool IsOpen => _manager.IsOpen(Note.Id);

    /// <summary>打开/关闭按钮文案。</summary>
    public string OpenCloseLabel => IsOpen ? "关闭" : "打开";

    /// <summary>打开/关闭独立窗口。
    /// </summary>
    public void ToggleOpen()
    {
        if (IsOpen)
        {
            _manager.Close(Note.Id);
        }
        else
        {
            _manager.Open(Note.Id);
        }

        _parent.RefreshAfterAction();
    }

    /// <summary>删除便签。
    /// </summary>
    public void Delete()
    {
        _manager.Delete(Note.Id);
        _parent.RefreshAfterAction();
    }

    /// <summary>刷新底层模型（窗口编辑后同步到列表）。
    /// </summary>
    public void RefreshNote(StickyNote note)
    {
        Note = note;
    }
}