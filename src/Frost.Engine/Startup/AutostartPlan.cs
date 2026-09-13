namespace Frost.Engine.Startup;

/// <summary>How autostart is registered.</summary>
public enum AutostartMechanism
{
    /// <summary>
    /// The MSIX <c>StartupTask</c> extension. Declared in the package manifest and
    /// removed with the package, so it cannot be orphaned.
    /// </summary>
    PackagedStartupTask = 0,

    /// <summary>
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, for an
    /// unpackaged build. Frost owns removing it.
    /// </summary>
    CurrentUserRunKey = 1,
}

/// <summary>
/// Decides how Frost registers itself to start with Windows, and what has to be
/// removed to undo it.
/// </summary>
/// <remarks>
/// <para><b>Task Scheduler is deliberately not an option.</b> It is the usual way
/// apps get "run at logon with elevation", and it is also the usual source of the
/// orphaned entries that outlive an uninstall: an MSIX package cannot remove a
/// scheduled task it created, so uninstalling would leave a task pointing at a
/// deleted executable, which Windows then reports as a failure at every logon.
/// Phase 10 requires uninstall to leave nothing behind, and the cheapest way to
/// guarantee that is to never create anything the uninstaller cannot reach.</para>
///
/// <para>So: the packaged build uses the <c>StartupTask</c> extension, which
/// Windows removes with the package and which the user can also disable in Task
/// Manager's Startup tab (where they will look for it). The unpackaged build uses
/// the per-user Run key, which Frost itself owns and removes.</para>
///
/// <para>Neither mechanism needs elevation, which is the other reason to avoid
/// Task Scheduler — a background recorder has no business asking for admin.</para>
/// </remarks>
public static class AutostartPlan
{
    /// <summary>Registry path holding per-user autostart entries.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name Frost uses under the Run key.</summary>
    public const string RunValueName = "Frost";

    /// <summary>
    /// Task id declared by the <c>StartupTask</c> extension in the package
    /// manifest. Must match the manifest exactly or enabling it fails at runtime.
    /// </summary>
    public const string StartupTaskId = "FrostEngineAutostart";

    /// <summary>Which mechanism to use.</summary>
    /// <param name="isPackaged">Whether the process is running from an MSIX package.</param>
    public static AutostartMechanism MechanismFor(bool isPackaged) =>
        isPackaged ? AutostartMechanism.PackagedStartupTask : AutostartMechanism.CurrentUserRunKey;

    /// <summary>
    /// The command line to put in the Run key.
    /// </summary>
    /// <remarks>
    /// Quoted whether or not the path contains a space. An unquoted path with a
    /// space is the classic Windows mis-launch: <c>C:\Program</c> is tried first,
    /// and on some systems that is an actual executable.
    /// </remarks>
    public static string BuildRunCommand(string executablePath, bool startMinimised = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            // A quote in a path is not legal on Windows, and passing it through
            // would let a crafted path inject arguments.
            throw new ArgumentException(
                "An executable path cannot contain a quote character.", nameof(executablePath));
        }

        var command = $"\"{executablePath}\"";
        return startMinimised ? $"{command} --tray" : command;
    }

    /// <summary>
    /// Everything an uninstall has to remove for a given mechanism.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than implied, so Phase 10's "leaves nothing behind" check
    /// has something concrete to verify against.
    /// </remarks>
    public static IReadOnlyList<AutostartArtifact> ArtifactsFor(AutostartMechanism mechanism) =>
        mechanism switch
        {
            AutostartMechanism.CurrentUserRunKey =>
            [
                new AutostartArtifact(
                    AutostartArtifactKind.RegistryValue,
                    $@"HKCU\{RunKeyPath}\{RunValueName}",
                    RemovedByUninstaller: false),
            ],

            AutostartMechanism.PackagedStartupTask =>
            [
                new AutostartArtifact(
                    AutostartArtifactKind.PackagedStartupTask,
                    StartupTaskId,
                    RemovedByUninstaller: true),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Unknown mechanism."),
        };

    /// <summary>
    /// Artifacts that an uninstaller will <i>not</i> clean up on its own, and
    /// which Frost must therefore remove itself before it goes.
    /// </summary>
    public static IReadOnlyList<AutostartArtifact> ArtifactsNeedingExplicitRemoval(
        AutostartMechanism mechanism) =>
        ArtifactsFor(mechanism).Where(a => !a.RemovedByUninstaller).ToList();
}

/// <summary>Something autostart leaves on the system.</summary>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Identifier">Where to find it.</param>
/// <param name="RemovedByUninstaller">
/// Whether removing the app also removes this. False means Frost has to.
/// </param>
public readonly record struct AutostartArtifact(
    AutostartArtifactKind Kind,
    string Identifier,
    bool RemovedByUninstaller);

/// <summary>Sorts of autostart artifact.</summary>
public enum AutostartArtifactKind
{
    RegistryValue = 0,
    PackagedStartupTask = 1,

    /// <summary>
    /// Never produced by Frost. Present only so the uninstall check can assert its
    /// absence.
    /// </summary>
    ScheduledTask = 2,
}
