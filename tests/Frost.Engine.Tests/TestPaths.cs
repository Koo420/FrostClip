using System.Reflection;

namespace Frost.Engine.Tests;

/// <summary>Repo-root lookup for the structural tests, baked in at build time.</summary>
internal static class TestPaths
{
    internal static string RepoRoot { get; } =
        typeof(TestPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "FrostRepoRoot")
            .Value
        ?? throw new InvalidOperationException("FrostRepoRoot metadata missing.");

    internal static string Read(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(relativeParts).ToArray()));

    internal static bool Exists(params string[] relativeParts) =>
        Path.Exists(Path.Combine(new[] { RepoRoot }.Concat(relativeParts).ToArray()));
}
