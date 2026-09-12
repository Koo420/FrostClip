namespace Frost.Engine.Windows;

/// <summary>
/// Windows process host for the Engine. Wired up incrementally by the build
/// phases; see PROGRESS.md.
/// </summary>
internal static class EngineHost
{
    internal static int Run(string[] args)
    {
        _ = args;
        return 0;
    }
}
