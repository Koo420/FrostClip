using System.Reflection;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Guards the structural constraints the rest of the build depends on. These are
/// cheap and they catch the mistake of "just referencing" a Windows-only type
/// from portable code, which would silently break the test host and the
/// platform-agnostic layering.
/// </summary>
public sealed class ArchitectureTests
{
    private static Assembly SharedAssembly => typeof(Frost.Shared.FrostInfo).Assembly;

    private static Assembly EngineAssembly => typeof(Frost.Engine.EngineInfo).Assembly;


    [Fact]
    public void SharedAssemblyIsPlatformAgnostic()
    {
        var referenced = SharedAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referenced, n =>
            n.Contains("WinRT", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Windows.SDK", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("Vortice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PortableEngineFlavourDoesNotDependOnWindowsInterop()
    {
        // The net8.0 flavour of Frost.Engine is what the test host loads. If a
        // Vortice / WinRT reference leaks into it, the Windows-only code has
        // escaped the Windows/ folder and this suite would stop being runnable
        // on a non-Windows host.
        var referenced = EngineAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referenced, n =>
            n.StartsWith("Vortice", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("WinRT", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("src", "Frost.Engine", "Frost.Engine.csproj")]
    [InlineData("src", "Frost.Shell", "Frost.Shell.csproj")]
    [InlineData("tests", "Frost.Engine.Tests", "Frost.Engine.Tests.csproj")]
    public void ProjectsAreWiredToShared(string a, string b, string c)
    {
        Assert.Contains("Frost.Shared.csproj", TestPaths.Read(a, b, c));
    }

    [Fact]
    public void SolutionContainsAllFourProjects()
    {
        var sln = TestPaths.Read("Frost.sln");
        Assert.Contains("Frost.Shared.csproj", sln);
        Assert.Contains("Frost.Engine.csproj", sln);
        Assert.Contains("Frost.Shell.csproj", sln);
        Assert.Contains("Frost.Engine.Tests.csproj", sln);
    }

    [Fact]
    public void RepoLayoutMatchesSpec()
    {
        Assert.True(TestPaths.Exists("src", "Frost.Engine"));
        Assert.True(TestPaths.Exists("src", "Frost.Shell"));
        Assert.True(TestPaths.Exists("src", "Frost.Shared"));
        Assert.True(TestPaths.Exists("tests", "Frost.Engine.Tests"));
        Assert.True(TestPaths.Exists("PROGRESS.md"));
        Assert.True(TestPaths.Exists("README.md"));
        Assert.True(TestPaths.Exists("build.ps1"));
    }
}
