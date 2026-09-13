using System.Xml.Linq;
using Frost.Engine.Startup;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Structural checks on the MSIX manifest.
/// </summary>
/// <remarks>
/// The manifest cannot be built on this host, but it can be read — and the things
/// most likely to break silently are exactly the ones a reader can check: an id
/// that has to match code, and the absence of autostart mechanisms an uninstall
/// could not clean up.
/// </remarks>
public sealed class FrostPackageTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    private static readonly XNamespace Uap5 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    private static readonly XNamespace Rescap =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static XDocument Manifest() =>
        XDocument.Parse(TestPaths.Read("src", "Frost.Package", "Package.appxmanifest"));

    [Fact]
    public void TheManifestIsWellFormedXml() => Assert.NotNull(Manifest().Root);

    [Fact]
    public void TheStartupTaskIdMatchesTheCode()
    {
        // StartupTask.GetAsync throws at runtime when these drift, and the
        // exception does not mention the id as the cause.
        var taskId = Manifest()
            .Descendants(Uap5 + "StartupTask")
            .Single()
            .Attribute("TaskId")!
            .Value;

        Assert.Equal(AutostartPlan.StartupTaskId, taskId);
    }

    [Fact]
    public void AutostartShipsDisabled()
    {
        // A recorder that runs at every logon without being asked is the
        // behaviour people uninstall over.
        var enabled = Manifest()
            .Descendants(Uap5 + "StartupTask")
            .Single()
            .Attribute("Enabled")!
            .Value;

        Assert.Equal("false", enabled);
    }

    [Fact]
    public void TheStartupTaskLaunchesTheEngineNotTheShell()
    {
        // The Shell is a window; what has to be running at logon is the recorder.
        var extension = Manifest()
            .Descendants(Uap5 + "Extension")
            .Single(e => e.Attribute("Category")?.Value == "windows.startupTask");

        Assert.Equal("Frost.Engine.exe", extension.Attribute("Executable")!.Value);
    }

    [Fact]
    public void StartupTaskIsTheOnlyAutostartMechanismDeclared()
    {
        // An MSIX package cannot remove a scheduled task or a Run key on
        // uninstall, and an orphan pointing at a deleted executable makes Windows
        // report a failure at every logon. Phase 10 requires uninstall to leave
        // nothing behind.
        //
        // Checked against the extension categories rather than the file's text,
        // so a comment mentioning what is avoided cannot fail the test.
        var categories = Manifest()
            .Descendants()
            .Where(e => e.Name.LocalName == "Extension")
            .Select(e => e.Attribute("Category")?.Value)
            .Where(c => c is not null)
            .ToList();

        Assert.Equal(["windows.startupTask"], categories);
    }

    [Fact]
    public void CaptureAndFullTrustAreDeclaredBecauseTheEngineNeedsThem()
    {
        var manifest = Manifest();

        Assert.Contains(
            manifest.Descendants(Uap + "Capability"),
            c => c.Attribute("Name")?.Value == "graphicsCapture");

        Assert.Contains(
            manifest.Descendants(Rescap + "Capability"),
            c => c.Attribute("Name")?.Value == "runFullTrust");
    }

    [Fact]
    public void TheMicrophoneIsTheOnlyDeviceCapability()
    {
        // Opt-in feature, and the only device Frost ever opens. Anything else here
        // would be a capability the user is asked to grant for no reason.
        var devices = Manifest()
            .Descendants(Foundation + "DeviceCapability")
            .Select(c => c.Attribute("Name")?.Value)
            .ToList();

        Assert.Equal(["microphone"], devices);
    }

    [Fact]
    public void OnlyTheCapabilitiesFrostActuallyNeedsAreRequested()
    {
        // Clips go to Videos or a folder the user picks, and the picker grants
        // access to both, so broadFileSystemAccess would be unjustifiable.
        // Asserted against the declared capability names rather than the file's
        // text, so a comment naming what is avoided cannot fail the test.
        var declared = Manifest()
            .Descendants()
            .Where(e => e.Name.LocalName is "Capability" or "DeviceCapability")
            .Select(e => e.Attribute("Name")?.Value)
            .ToList();

        Assert.Equal(
            ["graphicsCapture", "microphone", "runFullTrust"],
            declared.Order().ToList());
    }

    [Fact]
    public void TheMinimumWindowsVersionMatchesWhereGraphicsCaptureShipped()
    {
        // 10.0.18362 is 1903. Declaring anything lower would let the package
        // install on a machine where capture cannot work.
        var minVersion = Manifest()
            .Descendants(Foundation + "TargetDeviceFamily")
            .Single()
            .Attribute("MinVersion")!
            .Value;

        Assert.Equal("10.0.18362.0", minVersion);
    }

    [Fact]
    public void OnlyTheShellHasATile()
    {
        // The Engine is a tray process; a second tile for it would be confusing.
        var applications = Manifest().Descendants(Foundation + "Application").ToList();

        var application = Assert.Single(applications);
        Assert.Equal("FrostShell", application.Attribute("Id")!.Value);
        Assert.Equal("Frost.Shell.exe", application.Attribute("Executable")!.Value);
    }
}
