using Frost.Engine.Startup;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class AutostartPlanTests
{
    [Fact]
    public void APackagedBuildUsesTheStartupTaskExtension()
    {
        // Windows removes it with the package, and the user can disable it in Task
        // Manager's Startup tab, which is where they will look.
        Assert.Equal(
            AutostartMechanism.PackagedStartupTask,
            AutostartPlan.MechanismFor(isPackaged: true));
    }

    [Fact]
    public void AnUnpackagedBuildUsesThePerUserRunKey() =>
        Assert.Equal(
            AutostartMechanism.CurrentUserRunKey,
            AutostartPlan.MechanismFor(isPackaged: false));

    [Fact]
    public void TaskSchedulerIsNeverUsed()
    {
        // The classic source of entries that outlive an uninstall: an MSIX package
        // cannot remove a scheduled task it created, so uninstalling would leave a
        // task pointing at a deleted executable and Windows would report a failure
        // at every logon.
        foreach (var packaged in new[] { true, false })
        {
            var mechanism = AutostartPlan.MechanismFor(packaged);

            Assert.DoesNotContain(
                AutostartPlan.ArtifactsFor(mechanism),
                artifact => artifact.Kind == AutostartArtifactKind.ScheduledTask);
        }
    }

    [Fact]
    public void ThePackagedMechanismLeavesNothingForFrostToCleanUp()
    {
        var artifacts = AutostartPlan.ArtifactsFor(AutostartMechanism.PackagedStartupTask);

        Assert.All(artifacts, artifact => Assert.True(artifact.RemovedByUninstaller));
        Assert.Empty(
            AutostartPlan.ArtifactsNeedingExplicitRemoval(AutostartMechanism.PackagedStartupTask));
    }

    [Fact]
    public void TheRunKeyIsFrostsOwnResponsibility()
    {
        var artifact = Assert.Single(
            AutostartPlan.ArtifactsNeedingExplicitRemoval(AutostartMechanism.CurrentUserRunKey));

        Assert.Equal(AutostartArtifactKind.RegistryValue, artifact.Kind);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Frost", artifact.Identifier);
    }

    [Fact]
    public void EveryArtifactIsIdentifiedWellEnoughToFind()
    {
        foreach (var mechanism in new[]
                 {
                     AutostartMechanism.PackagedStartupTask,
                     AutostartMechanism.CurrentUserRunKey,
                 })
        {
            Assert.All(
                AutostartPlan.ArtifactsFor(mechanism),
                artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Identifier)));
        }
    }

    [Fact]
    public void AnUnknownMechanismIsRejectedRatherThanSilentlyEmpty() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AutostartPlan.ArtifactsFor((AutostartMechanism)99));

    [Fact]
    public void TheRunCommandIsQuotedEvenWithoutASpace()
    {
        // An unquoted path with a space is the classic Windows mis-launch:
        // C:\Program is tried first, and on some systems that is a real
        // executable. Quoting unconditionally removes the question.
        Assert.Equal(
            "\"C:\\Frost\\Frost.Engine.exe\" --tray",
            AutostartPlan.BuildRunCommand(@"C:\Frost\Frost.Engine.exe"));
    }

    [Fact]
    public void TheRunCommandQuotesAPathWithSpaces() =>
        Assert.Equal(
            "\"C:\\Program Files\\Frost\\Frost.Engine.exe\" --tray",
            AutostartPlan.BuildRunCommand(@"C:\Program Files\Frost\Frost.Engine.exe"));

    [Fact]
    public void StartingVisiblyOmitsTheTrayFlag() =>
        Assert.Equal(
            "\"C:\\Frost\\Frost.Engine.exe\"",
            AutostartPlan.BuildRunCommand(@"C:\Frost\Frost.Engine.exe", startMinimised: false));

    [Fact]
    public void APathContainingAQuoteIsRejected()
    {
        // Not legal on Windows, and passing it through would let a crafted path
        // inject arguments into the autostart command.
        Assert.Throws<ArgumentException>(
            () => AutostartPlan.BuildRunCommand("C:\\Frost\\evil\" --do-something-else\".exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyPathIsRejected(string? path) =>
        // ThrowsAny: a null path raises ArgumentNullException, which xUnit's
        // Throws<T> would reject for not being the exact type.
        Assert.ThrowsAny<ArgumentException>(() => AutostartPlan.BuildRunCommand(path!));

    [Fact]
    public void TheStartupTaskIdIsStable()
    {
        // It has to match the package manifest exactly; enabling it fails at
        // runtime otherwise, and the failure does not say why.
        Assert.Equal("FrostEngineAutostart", AutostartPlan.StartupTaskId);
    }
}
