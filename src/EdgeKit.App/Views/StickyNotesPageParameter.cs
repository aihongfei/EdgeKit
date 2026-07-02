using EdgeKit.App.ViewModels;

namespace EdgeKit.App.Views;

/// <summary>
/// 导航到 <see cref="StickyNotesPage"/> 时传入的参数。
/// </summary>
public sealed record StickyNotesPageParameter(StickyNotesViewModel ViewModel);