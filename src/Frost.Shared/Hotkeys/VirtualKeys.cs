namespace Frost.Shared.Hotkeys;

/// <summary>
/// Win32 virtual-key codes and their display names.
/// </summary>
/// <remarks>
/// Only the keys worth binding. Deliberately excludes the modifier keys
/// themselves (they live in <see cref="HotkeyModifiers"/>) and the keys a game
/// needs — no <c>W</c>, <c>A</c>, <c>S</c>, <c>D</c> is enforced here, but the
/// settings UI warns about single-key bindings without modifiers for exactly
/// that reason.
/// </remarks>
public static class VirtualKeys
{
    public const int F1 = 0x70;
    public const int F2 = 0x71;
    public const int F9 = 0x78;
    public const int F10 = 0x79;
    public const int F11 = 0x7A;
    public const int F12 = 0x7B;

    private static readonly (int Code, string Name)[] Table = BuildTable();

    /// <summary>Display name for a virtual-key code.</summary>
    public static string NameOf(int virtualKey)
    {
        foreach (var (code, name) in Table)
        {
            if (code == virtualKey)
            {
                return name;
            }
        }

        return $"VK_{virtualKey:X2}";
    }

    /// <summary>Virtual-key code for a display name.</summary>
    public static bool TryParse(string name, out int virtualKey)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var (code, candidate) in Table)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                virtualKey = code;
                return true;
            }
        }

        // Round-trips anything the table does not name, so an exotic key still
        // survives a save/load cycle.
        if (name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            virtualKey = parsed;
            return true;
        }

        virtualKey = 0;
        return false;
    }

    /// <summary>Every bindable key, for the settings UI.</summary>
    public static IReadOnlyList<(int Code, string Name)> All => Table;

    private static (int, string)[] BuildTable()
    {
        var table = new List<(int, string)>(128);

        for (var i = 0; i < 24; i++)
        {
            table.Add((0x70 + i, $"F{i + 1}"));
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            table.Add((c, c.ToString()));
        }

        for (var d = '0'; d <= '9'; d++)
        {
            table.Add((d, d.ToString()));
        }

        for (var i = 0; i <= 9; i++)
        {
            table.Add((0x60 + i, $"Num{i}"));
        }

        table.AddRange(new (int, string)[]
        {
            (0x08, "Backspace"),
            (0x09, "Tab"),
            (0x0D, "Enter"),
            (0x13, "Pause"),
            (0x14, "CapsLock"),
            (0x1B, "Escape"),
            (0x20, "Space"),
            (0x21, "PageUp"),
            (0x22, "PageDown"),
            (0x23, "End"),
            (0x24, "Home"),
            (0x25, "Left"),
            (0x26, "Up"),
            (0x27, "Right"),
            (0x28, "Down"),
            (0x2C, "PrintScreen"),
            (0x2D, "Insert"),
            (0x2E, "Delete"),
            (0x6A, "NumMultiply"),
            (0x6B, "NumAdd"),
            (0x6D, "NumSubtract"),
            (0x6E, "NumDecimal"),
            (0x6F, "NumDivide"),
            (0x90, "NumLock"),
            (0x91, "ScrollLock"),
            (0xBA, "Semicolon"),
            (0xBB, "Equals"),
            (0xBC, "Comma"),
            (0xBD, "Minus"),
            (0xBE, "Period"),
            (0xBF, "Slash"),
            (0xC0, "Backtick"),
            (0xDB, "LeftBracket"),
            (0xDC, "Backslash"),
            (0xDD, "RightBracket"),
            (0xDE, "Quote"),
        });

        return table.ToArray();
    }
}
