using EdgeKit.App.Windows;
using EdgeKit.Core.Notes;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace EdgeKit.App.Notes;

/// <summary>
/// 便签窗口生命周期管理服务。
/// 负责便签的创建、打开、关闭、删除，以及窗口位置/大小的持久化。
/// </summary>
public sealed class StickyNoteManagerService
{
    private const double DefaultWidth = 240;
    private const double DefaultHeight = 180;
    private const double Margin = 24;

    private readonly IStickyNoteRepository _repository;
    private readonly Dictionary<long, StickyNoteWindow> _openWindows = new();

    public StickyNoteManagerService(IStickyNoteRepository repository)
    {
        _repository = repository;
    }

    // 额外维护一份 handlers，用于在窗口打开/关闭（不触及仓库）时也能通知列表刷新。
    private EventHandler? _notesChangedHandlers;

    public event EventHandler? NotesChanged
    {
        add
        {
            _notesChangedHandlers += value;
            _repository.Changed += value;
        }
        remove
        {
            _notesChangedHandlers -= value;
            _repository.Changed -= value;
        }
    }

    private void NotifyChanged()
        => _notesChangedHandlers?.Invoke(this, EventArgs.Empty);

    /// <summary>获取所有便签（按更新时间倒序）。</summary>
    public IReadOnlyList<StickyNote> GetAllNotes() => _repository.GetAll();

    /// <summary>创建一条新便签并打开窗口。</summary>
    public StickyNote CreateNew()
    {
        var (x, y) = GetDefaultPosition();
        var note = new StickyNote(
            0,
            string.Empty,
            StickyNoteColors.DefaultHex,
            x,
            y,
            DefaultWidth,
            DefaultHeight,
            true,
            DateTime.UtcNow,
            DateTime.UtcNow);

        var id = _repository.Add(note);
        note = note with { Id = id };
        OpenWindow(note);
        return note;
    }

    /// <summary>打开指定便签的独立窗口；若已打开则前置激活。</summary>
    public void Open(long id)
    {
        if (_openWindows.TryGetValue(id, out var existing))
        {
            existing.Activate();
            return;
        }

        var note = _repository.GetById(id);
        if (note is not null)
        {
            OpenWindow(note);
        }
    }

    /// <summary>关闭指定便签窗口（不删除便签）。</summary>
    public void Close(long id)
    {
        if (_openWindows.TryGetValue(id, out var window))
        {
            CloseWindow(window);
        }
    }

    /// <summary>删除便签并关闭其窗口。</summary>
    public void Delete(long id)
    {
        if (_openWindows.TryGetValue(id, out var window))
        {
            CloseWindow(window);
        }

        _repository.Delete(id);
    }

    /// <summary>更新便签数据并同步到已打开的窗口。</summary>
    public void Update(StickyNote note)
    {
        _repository.Update(note);

        if (_openWindows.TryGetValue(note.Id, out var window))
        {
            window.UpdateNote(note);
        }
    }

    /// <summary>关闭所有便签窗口（应用退出时调用）。</summary>
    public void CloseAll()
    {
        foreach (var window in _openWindows.Values.ToList())
        {
            CloseWindow(window);
        }
    }

    /// <summary>判断指定便签当前是否已打开窗口。</summary>
    public bool IsOpen(long id) => _openWindows.ContainsKey(id);

    private void OpenWindow(StickyNote note)
    {
        if (_openWindows.ContainsKey(note.Id))
        {
            return;
        }

        var window = new StickyNoteWindow(note, _repository, OnWindowCloseRequested);
        _openWindows[note.Id] = window;
        window.Activate();
        window.ApplyInitialBounds();
        NotifyChanged();
    }

    private void OnWindowCloseRequested(StickyNoteWindow window)
    {
        CloseWindow(window);
    }

    private void CloseWindow(StickyNoteWindow window)
    {
        _openWindows.Remove(window.NoteId);
        window.DisposeResources();
        window.Close();
        NotifyChanged();
    }

    private static (double x, double y) GetDefaultPosition()
    {
        var area = DisplayArea.Primary;
        if (area is null)
        {
            return (100, 100);
        }

        var workArea = area.WorkArea;
        var x = workArea.X + workArea.Width - DefaultWidth - Margin;
        var y = workArea.Y + workArea.Height - DefaultHeight - Margin;
        return (x, y);
    }
}