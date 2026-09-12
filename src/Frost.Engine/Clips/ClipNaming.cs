using System.Text;

namespace Frost.Engine.Clips;

/// <summary>
/// Builds clip file names.
/// </summary>
/// <remarks>
/// Pure, so the awkward parts are testable: Windows' reserved device names,
/// characters a game title may contain that a path may not, trailing dots and
/// spaces that silently vanish from a Windows file name, and the duplicate
/// suffix when two clips land in the same second.
/// </remarks>
public static class ClipNaming
{
    /// <summary>Names Windows refuses regardless of extension.</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Longest name component we will produce, leaving room for a path.</summary>
    public const int MaxNameLength = 120;

    /// <summary>
    /// File name (no directory) for a clip.
    /// </summary>
    /// <param name="timestamp">When the clip was taken, in local time.</param>
    /// <param name="gameName">Foreground process or window name, if known.</param>
    /// <param name="label">Preset label, e.g. "30s".</param>
    /// <param name="extension">Container extension, with the dot.</param>
    public static string BuildFileName(
        DateTimeOffset timestamp,
        string? gameName = null,
        string? label = null,
        string extension = ".mp4")
    {
        var builder = new StringBuilder(64);

        var sanitisedGame = Sanitise(gameName);
        builder.Append(string.IsNullOrEmpty(sanitisedGame) ? "Frost" : sanitisedGame);

        // Sortable and unambiguous, with no characters a path cannot hold.
        builder.Append(' ');
        builder.Append(timestamp.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture));

        var sanitisedLabel = Sanitise(label);
        if (!string.IsNullOrEmpty(sanitisedLabel))
        {
            builder.Append(" (").Append(sanitisedLabel).Append(')');
        }

        var name = builder.ToString();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd();
        }

        return name + extension;
    }

    /// <summary>
    /// A path in <paramref name="directory"/> that does not already exist,
    /// appending " (2)", " (3)" and so on.
    /// </summary>
    /// <param name="exists">
    /// Existence probe, injected so the logic is testable without a file system.
    /// </param>
    public static string MakeUniquePath(string directory, string fileName, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(exists);

        var candidate = Path.Combine(directory, fileName);
        if (!exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        // Ten thousand clips in one second is not a real scenario, but silently
        // overwriting someone's recording would be unforgivable, so fall back to
        // something that cannot collide.
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Strips what a Windows file name cannot contain, and neutralises the
    /// reserved device names.
    /// </summary>
    public static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            // Control characters and the invalid set, plus the path separators
            // that Path.GetInvalidFileNameChars misses on some platforms.
            var invalid = char.IsControl(character) ||
                          character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

            if (invalid)
            {
                continue;
            }

            if (character == ' ')
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(character);
        }

        // Windows silently drops trailing dots and spaces, which turns
        // "Half-Life 2." into a name that does not round-trip.
        var result = builder.ToString().TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            return string.Empty;
        }

        if (Array.Exists(ReservedNames, reserved =>
                string.Equals(reserved, result, StringComparison.OrdinalIgnoreCase)))
        {
            return result + "_";
        }

        return result;
    }
}
