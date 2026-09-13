using Frost.Shared.Clips;

namespace Frost.Shared.Shell;

/// <summary>Why a rename was refused.</summary>
public enum RenameRejection
{
    None = 0,

    /// <summary>Nothing, or nothing but whitespace.</summary>
    Empty = 1,

    /// <summary>Characters Windows will not accept in a file name.</summary>
    InvalidCharacters = 2,

    /// <summary>A name Windows reserves device-wide, such as CON or NUL.</summary>
    ReservedName = 3,

    /// <summary>Long enough that the resulting path would be unusable.</summary>
    TooLong = 4,

    /// <summary>Another clip in the same folder already has this name.</summary>
    NameTaken = 5,

    /// <summary>A trailing dot or space, which Windows silently strips.</summary>
    TrailingDotOrSpace = 6,
}

/// <summary>
/// A rename that has been checked and is safe to perform.
/// </summary>
/// <param name="ClipPath">The video file to move.</param>
/// <param name="NewClipPath">Where it goes.</param>
/// <param name="SidecarPath">The metadata sidecar to move with it.</param>
/// <param name="NewSidecarPath">Where that goes.</param>
/// <param name="DisplayName">The name to store in the metadata.</param>
public readonly record struct RenamePlan(
    string ClipPath,
    string NewClipPath,
    string SidecarPath,
    string NewSidecarPath,
    string DisplayName);

/// <summary>Whether a rename can go ahead, and what it would do.</summary>
/// <param name="Rejection">Why not, or <see cref="RenameRejection.None"/>.</param>
/// <param name="Plan">Set when <paramref name="Rejection"/> is None.</param>
/// <param name="Message">Text for the UI when it was refused.</param>
public readonly record struct RenameCheck(
    RenameRejection Rejection,
    RenamePlan? Plan,
    string? Message)
{
    public bool IsAllowed => Rejection == RenameRejection.None;
}

/// <summary>
/// Validates a gallery rename before anything touches the disk.
/// </summary>
/// <remarks>
/// <para>Renaming a clip is two file moves, not one: the video and its
/// <c>.frost.json</c> sidecar. Getting that wrong is how a gallery ends up with
/// a clip that has lost its bookmarks and its game name, so the plan names both
/// paths and the caller moves the video first — a sidecar without a video is
/// invisible and harmless, a video without its sidecar loses data.</para>
///
/// <para>The validation is deliberately stricter than
/// <c>Path.GetInvalidFileNameChars</c>. Windows accepts a trailing dot or space
/// in an API call and then silently strips it, so "clutch." becomes "clutch" —
/// and a gallery that lets you type a name it then does not use looks broken.
/// Reserved device names are rejected for the same reason: <c>CON.mp4</c> fails
/// at the file-system layer with an error that does not mention reserved
/// names.</para>
///
/// <para>The Windows rules are also written out here rather than taken from
/// <see cref="Path"/>, which reports whatever the <i>host</i> runtime allows.
/// Frost validates names for a Windows file system no matter what it is compiled
/// on, so <c>Path.GetInvalidFileNameChars</c> would be the wrong question even
/// if this never ran anywhere else — and it is also what lets the rules be
/// tested on the Linux build host, where that call returns only <c>/</c> and NUL
/// and <see cref="Path.GetDirectoryName"/> does not split a backslash path at
/// all.</para>
/// </remarks>
public static class ClipRename
{
    /// <summary>
    /// Longest name accepted, leaving room for the folder, the extension and the
    /// sidecar's own suffix inside the classic 260-character path limit.
    /// </summary>
    public const int MaximumNameLength = 150;

    /// <summary>
    /// Characters a Windows file name cannot contain, control characters aside.
    /// </summary>
    private const string InvalidCharacters = @"<>:""/\|?*";

    /// <summary>
    /// Names Windows reserves for devices, in any folder and with any extension.
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Checks a proposed name against the clip's folder.
    /// </summary>
    /// <param name="clip">The clip being renamed.</param>
    /// <param name="proposedName">
    /// The new name, without an extension — the gallery shows names without one
    /// and re-attaching the original extension is not the user's job.
    /// </param>
    /// <param name="nameExists">
    /// Whether a given full path is already taken. Injected so the check is
    /// testable without a file system, and so the caller can use whatever it
    /// already knows about the folder rather than stat-ing on every keystroke.
    /// </param>
    public static RenameCheck Check(
        ClipMetadata clip,
        string? proposedName,
        Func<string, bool> nameExists)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(nameExists);

        if (string.IsNullOrWhiteSpace(proposedName))
        {
            return Refuse(RenameRejection.Empty, "A clip needs a name.");
        }

        var name = proposedName.Trim();

        if (name.Length > MaximumNameLength)
        {
            return Refuse(
                RenameRejection.TooLong,
                $"Names are limited to {MaximumNameLength} characters.");
        }

        // Checked against the untrimmed input as well: Windows strips these
        // silently, so accepting "clutch." and storing "clutch" means the gallery
        // shows a name the user did not type.
        if (proposedName.EndsWith('.') || proposedName.EndsWith(' '))
        {
            return Refuse(
                RenameRejection.TrailingDotOrSpace,
                "Names cannot end with a dot or a space.");
        }

        if (ContainsInvalidCharacter(name))
        {
            // Listed explicitly rather than "invalid characters": the set is not
            // guessable and people retype the same slash three times.
            return Refuse(
                RenameRejection.InvalidCharacters,
                @"Names cannot contain \ / : * ? "" < > |");
        }

        if (IsReserved(name))
        {
            return Refuse(
                RenameRejection.ReservedName,
                $"'{name}' is a name Windows reserves for a device.");
        }

        var (directory, separator) = SplitDirectory(clip.FilePath);
        var extension = ExtensionOf(clip.FilePath);
        var newClipPath = directory.Length == 0
            ? name + extension
            : directory + separator + name + extension;

        // Renaming a clip to the name it already has is a no-op the UI should
        // accept silently, not a collision with itself.
        var isSamePath = string.Equals(
            newClipPath,
            clip.FilePath,
            StringComparison.OrdinalIgnoreCase);

        if (!isSamePath && nameExists(newClipPath))
        {
            return Refuse(
                RenameRejection.NameTaken,
                $"There is already a clip called '{name}'.");
        }

        return new RenameCheck(
            RenameRejection.None,
            new RenamePlan(
                ClipPath: clip.FilePath,
                NewClipPath: newClipPath,
                SidecarPath: ClipMetadata.SidecarPathFor(clip.FilePath),
                NewSidecarPath: ClipMetadata.SidecarPathFor(newClipPath),
                DisplayName: name),
            Message: null);
    }

    /// <summary>Whether performing this plan would move anything.</summary>
    /// <remarks>
    /// A rename that only changes the display name — the same file name, a
    /// different label — must not move files, and a rename to the identical name
    /// must not do anything at all.
    /// </remarks>
    public static bool IsNoOp(RenamePlan plan) =>
        string.Equals(plan.ClipPath, plan.NewClipPath, StringComparison.Ordinal);

    /// <summary>Applies a checked rename to the metadata record.</summary>
    public static ClipMetadata Apply(ClipMetadata clip, RenamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(clip);

        return clip with
        {
            FilePath = plan.NewClipPath,
            DisplayName = plan.DisplayName,
        };
    }

    private static bool ContainsInvalidCharacter(string name)
    {
        foreach (var character in name)
        {
            // Control characters are rejected wholesale: they are invisible in
            // the edit box and produce a file name nobody can select afterwards.
            if (char.IsControl(character) || InvalidCharacters.Contains(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a path into its folder and the separator it uses.
    /// </summary>
    /// <remarks>
    /// Hand-rolled for the same reason as the character set: on a non-Windows
    /// host <see cref="Path.GetDirectoryName"/> treats a backslash as an
    /// ordinary character and returns the whole path as the file name.
    /// </remarks>
    private static (string Directory, char Separator) SplitDirectory(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);

        return index < 0
            ? (string.Empty, '\\')
            : (path[..index], path[index]);
    }

    private static string ExtensionOf(string path)
    {
        var name = FileNameOf(path);
        var dot = name.LastIndexOf('.');

        return dot <= 0 ? string.Empty : name[dot..];
    }

    private static string FileNameOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static bool IsReserved(string name)
    {
        // Reserved with or without an extension: "CON" and "CON.mp4" both fail.
        var dot = name.LastIndexOf('.');
        var stem = dot <= 0 ? name : name[..dot];

        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RenameCheck Refuse(RenameRejection rejection, string message) =>
        new(rejection, null, message);
}
