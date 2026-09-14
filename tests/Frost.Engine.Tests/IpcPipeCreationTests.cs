using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// Guards the lesson from the bug that stopped the Engine opening its pipe on
/// Windows while every test here passed.
/// </summary>
/// <remarks>
/// <para>The cause was not the ACL code being wrong in a subtle way — it threw
/// on the first call. The cause was that it sat behind <c>#if WINDOWS</c>, so it
/// compiled on the build host and never executed, and the round-trip tests
/// silently exercised the other branch. A platform-conditional branch in the
/// pipe-creation path is untestable by construction, and this is the one place
/// in the Engine where that is worth asserting: if the pipe cannot be created,
/// the Shell can never connect to a running Engine at all.</para>
///
/// <para>These read the source with comments stripped. The first version did not,
/// and matched the doc comment that explains the bug - so writing down the fix
/// broke the test guarding it.</para>
/// </remarks>
public sealed class IpcPipeCreationTests
{
    /// <summary>
    /// A source file with its comments removed.
    /// </summary>
    /// <remarks>
    /// Necessary, not tidiness: the first version of the test below matched the
    /// doc comment that explains the bug, so documenting the fix broke the test
    /// guarding it. A structural test has to read code, not prose.
    /// </remarks>
    private static string CodeOf(params string[] relativeParts) =>
        string.Join(
            '\n',
            TestPaths.Read(relativeParts)
                .Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                    && !line.StartsWith("*", StringComparison.Ordinal)));

    [Fact]
    public void ThePipeIsCreatedTheSameWayOnEveryPlatform()
    {
        var source = CodeOf("src", "Frost.Engine", "Ipc", "IpcServer.cs");

        var directives = source
            .Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => line.StartsWith("#if", StringComparison.Ordinal)
                || line.StartsWith("#else", StringComparison.Ordinal)
                || line.StartsWith("#elif", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            directives.Count == 0,
            "IpcServer.cs contains a conditional-compilation branch: "
                + string.Join(", ", directives)
                + ". A platform-conditional pipe-creation path cannot be executed "
                + "by this test suite, which is exactly how the Engine shipped a "
                + "pipe it could not open on Windows while every IPC test passed.");
    }

    [Fact]
    public void BothEndsOfThePipeRestrictAccessToTheCurrentUser()
    {
        // CurrentUserOnly is load-bearing at both ends and for different
        // reasons. On the server it replaces a default DACL that is too
        // generous for a channel accepting "write a file here". On the client it
        // makes .NET verify the pipe's owner, without which any process could
        // create a pipe of this name first and receive the Shell's traffic.
        var server = CodeOf("src", "Frost.Engine", "Ipc", "IpcServer.cs");
        var client = CodeOf("src", "Frost.Shared", "Ipc", "IpcClient.cs");

        Assert.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly", server);
        Assert.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly", client);
    }

    [Fact]
    public void TheServerNoLongerCombinesAnExplicitAclWithCurrentUserOnly()
    {
        // The illegal combination itself: .NET throws ArgumentException because
        // the two express the same restriction, and the Engine then cannot open
        // its pipe at all.
        var source = CodeOf("src", "Frost.Engine", "Ipc", "IpcServer.cs");

        Assert.DoesNotContain("NamedPipeServerStreamAcl", source);
        Assert.DoesNotContain("new PipeSecurity()", source);
    }
}
