namespace EdgeKit.App.Interaction;

public enum TrayMenuItemKind
{
    Command,
    Separator
}

public sealed record TrayMenuItem(uint Id, string? Text, string? Glyph, TrayMenuItemKind Kind, Action? Execute = null)
{
    public static TrayMenuItem Command(uint id, string text, string glyph, Action execute)
        => new(id, text, glyph, TrayMenuItemKind.Command, execute);

    public static TrayMenuItem Separator()
        => new(0, null, null, TrayMenuItemKind.Separator);
}