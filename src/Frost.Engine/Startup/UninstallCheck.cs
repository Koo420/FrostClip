namespace Frost.Engine.Startup;

/// <summary>Something found on the system after Frost should be gone.</summary>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Location">Where it was found.</param>
/// <param name="IsOrphan">
/// Whether it counts as a failure. Some leftovers are deliberate.
/// </param>
/// <param name="Note">Why it does or does not count.</param>
public readonly record struct UninstallLeftover(
    UninstallArtifactKind Kind,
    string Location,
    bool IsOrphan,
    string Note);

/// <summary>Things worth looking for after an uninstall.</summary>
public enum UninstallArtifactKind
{
    /// <summary>A Run-key value. An orphan if present.</summary>
    RegistryAutostart = 0,

    /// <summary>A scheduled task. Always an orphan — Frost never creates one.</summary>
    ScheduledTask = 1,

    /// <summary>Settings under %AppData%. Deliberately kept.</summary>
    Settings = 2,

    /// <summary>Recorded clips. Deliberately kept.</summary>
    Clips = 3,

    /// <summary>A log file. Deliberately kept, briefly.</summary>
    Logs = 4,
}

/// <summary>
/// Decides what counts as an orphan after Frost is uninstalled.
/// </summary>
/// <remarks>
/// <para>The distinction the spec's requirement turns on: "leaves no orphaned
/// scheduled tasks / registry autostart entries" is about things that keep
/// <i>running</i> or keep <i>pointing</i> at an executable that no longer exists.
/// A stale Run key makes Windows try to launch a deleted binary at every logon; a
/// leftover scheduled task does the same and shows up as a failure in Task
/// Scheduler. Those are bugs.</para>
///
/// <para>A user's clips and settings are not. Deleting someone's recordings
/// because they uninstalled the recorder would be indefensible, and wiping their
/// hotkey configuration means a reinstall starts from scratch. So those are
/// reported as found-and-intentional rather than as failures, and the check is
/// explicit about which is which so "we left files behind" cannot be confused
/// with "we left a broken autostart behind".</para>
/// </remarks>
public static class UninstallCheck
{
    /// <summary>Whether a kind of leftover is a failure.</summary>
    public static bool IsOrphan(UninstallArtifactKind kind) => kind switch
    {
        // Points at an executable that is gone. Windows retries at every logon.
        UninstallArtifactKind.RegistryAutostart => true,

        // Frost never creates one, so finding one means something went wrong.
        UninstallArtifactKind.ScheduledTask => true,

        // Deliberately kept: a reinstall should not start from scratch.
        UninstallArtifactKind.Settings => false,

        // Deliberately kept: deleting someone's recordings because they
        // uninstalled the recorder would be indefensible.
        UninstallArtifactKind.Clips => false,

        UninstallArtifactKind.Logs => false,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artifact kind."),
    };

    /// <summary>Why a kind is or is not a failure, for the report.</summary>
    public static string NoteFor(UninstallArtifactKind kind) => kind switch
    {
        UninstallArtifactKind.RegistryAutostart =>
            "orphan: Windows will try to launch a deleted executable at every logon",
        UninstallArtifactKind.ScheduledTask =>
            "orphan: Frost never creates scheduled tasks, so this should not exist",
        UninstallArtifactKind.Settings =>
            "kept on purpose, so a reinstall does not start from scratch",
        UninstallArtifactKind.Clips =>
            "kept on purpose: these are the user's recordings",
        UninstallArtifactKind.Logs =>
            "kept on purpose, for diagnosing an install that went wrong",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artifact kind."),
    };

    /// <summary>Builds a leftover record for something that was found.</summary>
    public static UninstallLeftover Found(UninstallArtifactKind kind, string location) =>
        new(kind, location, IsOrphan(kind), NoteFor(kind));

    /// <summary>
    /// Renders a report and says whether the uninstall was clean.
    /// </summary>
    /// <remarks>
    /// Clean means no orphans. Intentional leftovers are listed so the reader can
    /// see the check actually looked, rather than wondering whether an empty
    /// report means "nothing found" or "nothing checked".
    /// </remarks>
    public static (bool IsClean, string Report) Summarise(IReadOnlyList<UninstallLeftover> found)
    {
        ArgumentNullException.ThrowIfNull(found);

        var orphans = found.Where(f => f.IsOrphan).ToList();
        var intentional = found.Where(f => !f.IsOrphan).ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Uninstall check");
        builder.AppendLine("===============");

        if (orphans.Count == 0)
        {
            builder.AppendLine("  No orphaned autostart entries or scheduled tasks.");
        }
        else
        {
            foreach (var orphan in orphans)
            {
                builder.AppendLine($"  ORPHAN  {orphan.Kind} at {orphan.Location} — {orphan.Note}");
            }
        }

        foreach (var item in intentional)
        {
            builder.AppendLine($"  kept    {item.Kind} at {item.Location} — {item.Note}");
        }

        builder.AppendLine();
        builder.AppendLine(
            orphans.Count == 0
                ? "  Clean."
                : $"  {orphans.Count} orphan(s) found; uninstall did not clean up.");

        return (orphans.Count == 0, builder.ToString());
    }
}
