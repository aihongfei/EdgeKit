using System.Collections.ObjectModel;
using EdgeKit.App.Notes;
using EdgeKit.Core.Notes;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

    /// <summary>更新时间的紧凑展示。</summary>
    public string UpdatedText => Note.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>该便签当前是否已打开独立窗口。</summary>
    public bool IsOpen => _manager.IsOpen(Note.Id);

    /// <summary>当前打开状态标签。</summary>
    public string StatusLabel => IsOpen ? "已打开" : "已收起";

    /// <summary>打开/关闭按钮文案。</summary>
    public string OpenCloseLabel => IsOpen ? "关闭" : "打开";

    /// <summary>打开/关闭按钮图标。</summary>
    public string OpenCloseGlyph => IsOpen ? "\uE711" : "\uE8A7";

    /// <summary>便签颜色在 Dense Glass 卡片中的色块。</summary>
    public SolidColorBrush ColorBrush => new(ParseColor(Note.ColorHex));

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

    private static Color ParseColor(string hex)
    {
        var span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length == 8
            && byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(span[6..8], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb((byte)Math.Max((int)a, 190), r, g, b);
        }

        if (span.Length == 6
            && byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out var rr)
            && byte.TryParse(span[2..4], System.Globalization.NumberStyles.HexNumber, null, out var gg)
            && byte.TryParse(span[4..6], System.Globalization.NumberStyles.HexNumber, null, out var bb))
        {
            return Color.FromArgb(220, rr, gg, bb);
        }

        return Color.FromArgb(220, 26, 36, 45);
    }
}
