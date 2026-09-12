namespace Frost.Shared.Hotkeys;

/// <summary>Modifier keys, as a set.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,

    /// <summary>Either Windows key. Usable, but a poor choice for a game hotkey.</summary>
    Windows = 8,
}

/// <summary>
/// A key combination, stored as a Win32 virtual-key code plus modifiers.
/// </summary>
/// <remarks>
/// Virtual-key codes rather than characters, because the binding must mean the
/// same physical key regardless of the user's keyboard layout — and because the
/// low-level keyboard hook reports virtual keys.
/// </remarks>
public readonly record struct HotkeyBinding(int VirtualKey, HotkeyModifiers Modifiers = HotkeyModifiers.None)
{
    /// <summary>Nothing bound.</summary>
    public static HotkeyBinding None => default;

    public bool IsBound => VirtualKey != 0;

    /// <summary>Human-readable form, e.g. <c>Ctrl+Shift+F10</c>.</summary>
    public override string ToString()
    {
        if (!IsBound)
        {
            return "(unbound)";
        }

        var parts = new List<string>(4);

        // Fixed order so a binding always renders the same way, whatever order
        // the flags were set in.
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(VirtualKeys.NameOf(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Parses the form <see cref="ToString"/> produces.</summary>
    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var keyName = (string?)null;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
                default:
                    if (keyName is not null)
                    {
                        // Two non-modifier keys is not a combination we support.
                        return false;
                    }

                    keyName = part;
                    continue;
            }
        }

        if (keyName is null || !VirtualKeys.TryParse(keyName, out var virtualKey))
        {
            return false;
        }

        binding = new HotkeyBinding(virtualKey, modifiers);
        return true;
    }
}
