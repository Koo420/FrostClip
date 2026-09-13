using Frost.Engine.Startup;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class UninstallCheckTests
{
    [Fact]
    public void AStaleAutostartEntryIsAnOrphan()
    {
        // It makes Windows try to launch a deleted executable at every logon.
        Assert.True(UninstallCheck.IsOrphan(UninstallArtifactKind.RegistryAutostart));
        Assert.Contains("every logon", UninstallCheck.NoteFor(UninstallArtifactKind.RegistryAutostart));
    }

    [Fact]
    public void AScheduledTaskIsAlwaysAnOrphanBecauseFrostNeverCreatesOne()
    {
        Assert.True(UninstallCheck.IsOrphan(UninstallArtifactKind.ScheduledTask));
        Assert.Contains("never creates", UninstallCheck.NoteFor(UninstallArtifactKind.ScheduledTask));
    }

    [Theory]
    [InlineData(UninstallArtifactKind.Settings)]
    [InlineData(UninstallArtifactKind.Clips)]
    [InlineData(UninstallArtifactKind.Logs)]
    public void UserDataIsNotAnOrphan(UninstallArtifactKind kind)
    {
        // Deleting someone's recordings because they uninstalled the recorder
        // would be indefensible, and wiping their hotkeys means a reinstall starts
        // from scratch.
        Assert.False(UninstallCheck.IsOrphan(kind));
        Assert.Contains("on purpose", UninstallCheck.NoteFor(kind));
    }

    [Fact]
    public void AnUnknownKindIsRejectedRatherThanTreatedAsHarmless()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UninstallCheck.IsOrphan((UninstallArtifactKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => UninstallCheck.NoteFor((UninstallArtifactKind)99));
    }

    [Fact]
    public void FindingNothingIsClean()
    {
        var (clean, report) = UninstallCheck.Summarise([]);

        Assert.True(clean);
        Assert.Contains("No orphaned autostart entries", report);
        Assert.Contains("Clean.", report);
    }

    [Fact]
    public void UserDataAloneIsStillClean()
    {
        var (clean, report) = UninstallCheck.Summarise(
        [
            UninstallCheck.Found(UninstallArtifactKind.Settings, @"%AppData%\Frost\settings.json"),
            UninstallCheck.Found(UninstallArtifactKind.Clips, @"C:\Users\x\Videos\Frost"),
        ]);

        Assert.True(clean);
        Assert.Contains("Clean.", report);

        // And it says it looked, rather than leaving the reader wondering whether
        // an empty report means "nothing found" or "nothing checked".
        Assert.Contains("kept    Settings", report);
        Assert.Contains("kept    Clips", report);
    }

    [Fact]
    public void AnOrphanMakesTheCheckFail()
    {
        var (clean, report) = UninstallCheck.Summarise(
        [
            UninstallCheck.Found(
                UninstallArtifactKind.RegistryAutostart,
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Frost"),
            UninstallCheck.Found(UninstallArtifactKind.Clips, @"C:\Users\x\Videos\Frost"),
        ]);

        Assert.False(clean);
        Assert.Contains("ORPHAN  RegistryAutostart", report);
        Assert.Contains("1 orphan(s) found", report);

        // The user's clips are still reported as kept, not lumped in with the
        // failure.
        Assert.Contains("kept    Clips", report);
    }

    [Fact]
    public void SeveralOrphansAreAllReported()
    {
        var (clean, report) = UninstallCheck.Summarise(
        [
            UninstallCheck.Found(UninstallArtifactKind.RegistryAutostart, "run-key"),
            UninstallCheck.Found(UninstallArtifactKind.ScheduledTask, @"\Frost\Autostart"),
        ]);

        Assert.False(clean);
        Assert.Contains("2 orphan(s)", report);
        Assert.Contains("ScheduledTask", report);
    }

    [Fact]
    public void FoundRecordsCarryTheirLocationAndReason()
    {
        var leftover = UninstallCheck.Found(UninstallArtifactKind.RegistryAutostart, "somewhere");

        Assert.Equal("somewhere", leftover.Location);
        Assert.True(leftover.IsOrphan);
        Assert.False(string.IsNullOrWhiteSpace(leftover.Note));
    }

    [Fact]
    public void NullInputIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => UninstallCheck.Summarise(null!));

    [Fact]
    public void TheOrphanKindsAreExactlyWhatTheSpecForbids()
    {
        // "leaves no orphaned scheduled tasks / registry autostart entries" -
        // those two and nothing else.
        var orphanKinds = Enum.GetValues<UninstallArtifactKind>()
            .Where(UninstallCheck.IsOrphan)
            .Order()
            .ToList();

        Assert.Equal(
            [UninstallArtifactKind.RegistryAutostart, UninstallArtifactKind.ScheduledTask],
            orphanKinds);
    }
}
