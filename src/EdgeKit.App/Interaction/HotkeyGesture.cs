using EdgeKit.Native;
using Windows.System;

namespace EdgeKit.App.Interaction;

public readonly record struct HotkeyGesture(
    bool Control,
    bool Alt,
    bool Shift,
    bool Win,
    VirtualKey Key)
{
    public bool HasModifier => Control || Alt || Shift || Win;

    public uint Modifiers
    {
        get
        {
            uint modifiers = NativeMethods.MOD_NOREPEAT;
            if (Control)
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }

            if (Alt)
            {
                modifiers |= NativeMethods.MOD_ALT;
            }

            if (Shift)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            if (Win)
            {
                modifiers |= NativeMethods.MOD_WIN;
            }

            return modifiers;
        }
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Win)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && TryParseKey(parts[0], out var singleKey))
        {
            gesture = new HotkeyGesture(false, false, false, false, singleKey);
            return true;
        }

        var control = false;
        var alt = false;
        var shift = false;
        var win = false;
        VirtualKey? key = null;

        foreach (var rawPart in parts)
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    control = true;
                    break;
                case "ALT":
                    alt = true;
                    break;
                case "SHIFT":
                    shift = true;
                    break;
                case "WIN":
                case "WINDOWS":
                    win = true;
                    break;
                default:
                    if (!TryParseKey(rawPart, out var parsedKey) || IsModifierKey(parsedKey))
                    {
                        return false;
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(control, alt, shift, win, key.Value);
        return true;
    }

    public static bool TryCreateFromCurrentKeyboardState(VirtualKey key, out HotkeyGesture gesture)
    {
        gesture = default;
        if (IsModifierKey(key))
        {
            return false;
        }

        var control = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var shift = IsKeyDown(VirtualKey.Shift);
        var win = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);

        gesture = new HotkeyGesture(control, alt, shift, win, key);
        return true;
    }

    public static bool TryCreateModifierOnly(VirtualKey key, out HotkeyGesture gesture)
    {
        gesture = default;
        if (!IsModifierKey(key))
        {
            return false;
        }

        gesture = new HotkeyGesture(false, false, false, false, NormalizeModifierKey(key));
        return true;
    }

    private static bool TryParseKey(string raw, out VirtualKey key)
    {
        switch (raw.ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                key = VirtualKey.Control;
                return true;
            case "ALT":
                key = VirtualKey.Menu;
                return true;
            case "SHIFT":
                key = VirtualKey.Shift;
                return true;
            case "WIN":
            case "WINDOWS":
                key = VirtualKey.LeftWindows;
                return true;
            case "ESC":
            case "ESCAPE":
                key = VirtualKey.Escape;
                return true;
            case "SPACE":
                key = VirtualKey.Space;
                return true;
            case "BACKSPACE":
                key = VirtualKey.Back;
                return true;
        }

        var normalized = raw.Length == 1 && char.IsLetterOrDigit(raw[0])
            ? raw.ToUpperInvariant()
            : raw;

        return Enum.TryParse(normalized, ignoreCase: true, out key);
    }

    public static bool IsAnyModifierDown()
        => IsKeyDown(VirtualKey.Control)
            || IsKeyDown(VirtualKey.Menu)
            || IsKeyDown(VirtualKey.Shift)
            || IsKeyDown(VirtualKey.LeftWindows)
            || IsKeyDown(VirtualKey.RightWindows);

    private static bool IsKeyDown(VirtualKey key)
        => (NativeMethods.GetKeyState((int)key) & unchecked((short)0x8000)) != 0;

    public static bool IsModifierKey(VirtualKey key)
        => key is VirtualKey.Control
            or VirtualKey.LeftControl
            or VirtualKey.RightControl
            or VirtualKey.Menu
            or VirtualKey.LeftMenu
            or VirtualKey.RightMenu
            or VirtualKey.Shift
            or VirtualKey.LeftShift
            or VirtualKey.RightShift
            or VirtualKey.LeftWindows
            or VirtualKey.RightWindows;

    private static VirtualKey NormalizeModifierKey(VirtualKey key)
        => key switch
        {
            VirtualKey.LeftControl or VirtualKey.RightControl => VirtualKey.Control,
            VirtualKey.LeftMenu or VirtualKey.RightMenu => VirtualKey.Menu,
            VirtualKey.LeftShift or VirtualKey.RightShift => VirtualKey.Shift,
            _ => key
        };

    private static string FormatKey(VirtualKey key)
        => key switch
        {
            VirtualKey.Menu => "Alt",
            VirtualKey.Control => "Ctrl",
            VirtualKey.Space => "Space",
            VirtualKey.Escape => "Esc",
            VirtualKey.Back => "Backspace",
            VirtualKey.LeftWindows or VirtualKey.RightWindows => "Win",
            _ => key.ToString()
        };
}
