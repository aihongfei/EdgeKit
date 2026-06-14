using EdgeKit.Services.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgeKit.App.ViewModels;

/// <summary>首页活动窗口卡片的展示模型。</summary>
public sealed class ManagedWindowViewModel
{
    public ManagedWindowViewModel(ManagedWindowEntry model, BitmapImage? iconImage)
    {
        Model = model;
        IconImage = iconImage;
    }

    public ManagedWindowEntry Model { get; }

    public BitmapImage? IconImage { get; }

    public nint Hwnd => Model.Hwnd;

    public string Title => Model.Title;

    public string SubTitle => $"{Model.ProcessName} · PID {Model.ProcessIdText}";

    public string Glyph => "\uE737";

    public bool HasIconImage => IconImage is not null;

    public bool HasNoIconImage => IconImage is null;

    public bool HasSameDisplay(ManagedWindowEntry other)
        => Hwnd == other.Hwnd
            && string.Equals(Model.Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Model.ProcessName, other.ProcessName, StringComparison.Ordinal)
            && Model.ProcessId == other.ProcessId;
}
