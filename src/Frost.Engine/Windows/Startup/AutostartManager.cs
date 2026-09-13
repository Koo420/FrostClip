using Frost.Engine.Diagnostics;
using Frost.Engine.Startup;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Frost.Engine.Windows.Startup;

/// <summary>Why autostart could not be turned on.</summary>
public enum AutostartResult
{
    Enabled = 0,
    Disabled = 1,

    /// <summary>The user turned it off in Task Manager's Startup tab.</summary>
    DisabledByUser = 2,

    /// <summary>Group policy forbids it.</summary>
    DisabledByPolicy = 3,

    /// <summary>Something else went wrong; see the log.</summary>
    Failed = 4,
}

/// <summary>
/// Turns "start with Windows" on and off.
/// </summary>
/// <remarks>
/// Two mechanisms, chosen by <see cref="AutostartPlan"/>: the MSIX
/// <c>StartupTask</c> extension when packaged, the per-user Run key otherwise.
/// <para>
/// The packaged path has a wrinkle worth knowing: once the user disables the
/// entry in Task Manager's Startup tab, the app is not allowed to re-enable it,
/// and <c>RequestEnableAsync</c> returns <c>DisabledByUser</c> rather than
/// failing. That is correct behaviour and Frost reports it honestly rather than
/// leaving a settings toggle that silently springs back.
/// </para>
/// </remarks>
internal sealed class AutostartManager
{
    private readonly IEngineLog _log;
    private readonly bool _isPackaged;

    internal AutostartManager(IEngineLog log, bool? isPackaged = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _isPackaged = isPackaged ?? DetectPackaged();
    }

    internal AutostartMechanism Mechanism => AutostartPlan.MechanismFor(_isPackaged);

    /// <summary>Whether Frost is currently set to start with Windows.</summary>
    internal bool IsEnabled()
    {
        try
        {
            return Mechanism == AutostartMechanism.PackagedStartupTask
                ? IsStartupTaskEnabled()
                : ReadRunValue() is not null;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not read the autostart setting.", ex);
            return false;
        }
    }

    /// <summary>Turns autostart on or off, reporting what actually happened.</summary>
    internal AutostartResult Set(bool enabled, string? executablePath = null)
    {
        try
        {
            return Mechanism == AutostartMechanism.PackagedStartupTask
                ? SetStartupTask(enabled)
                : SetRunValue(enabled, executablePath);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not {(enabled ? "enable" : "disable")} autostart.", ex);
            return AutostartResult.Failed;
        }
    }

    /// <summary>
    /// Removes anything an uninstaller would not, so nothing is left pointing at a
    /// deleted executable.
    /// </summary>
    /// <remarks>
    /// Called on uninstall and also whenever the Engine notices the Run key points
    /// somewhere it is not: a moved or reinstalled build would otherwise leave a
    /// stale entry that fails silently at every logon.
    /// </remarks>
    internal void RemoveOrphans()
    {
        foreach (var artifact in AutostartPlan.ArtifactsNeedingExplicitRemoval(Mechanism))
        {
            if (artifact.Kind != AutostartArtifactKind.RegistryValue)
            {
                continue;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutostartPlan.RunKeyPath, writable: true);
                if (key?.GetValue(AutostartPlan.RunValueName) is not null)
                {
                    key.DeleteValue(AutostartPlan.RunValueName, throwOnMissingValue: false);
                    _log.Info($"Removed the autostart entry {artifact.Identifier}.");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove the autostart entry {artifact.Identifier}.", ex);
            }
        }
    }

    /// <summary>
    /// Whether the Run key points at this executable.
    /// </summary>
    /// <remarks>
    /// A build that was moved or reinstalled elsewhere leaves an entry that fails
    /// at logon with no visible error, so the Engine checks and rewrites it.
    /// </remarks>
    internal bool IsRunValueStale(string executablePath)
    {
        if (Mechanism != AutostartMechanism.CurrentUserRunKey)
        {
            return false;
        }

        var existing = ReadRunValue();

        if (existing is null)
        {
            return false;
        }

        return !existing.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    private string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartPlan.RunKeyPath);
        return key?.GetValue(AutostartPlan.RunValueName) as string;
    }

    private AutostartResult SetRunValue(bool enabled, string? executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutostartPlan.RunKeyPath, writable: true);

        if (key is null)
        {
            _log.Error("Could not open the per-user Run key.");
            return AutostartResult.Failed;
        }

        if (!enabled)
        {
            key.DeleteValue(AutostartPlan.RunValueName, throwOnMissingValue: false);
            _log.Info("Autostart disabled.");
            return AutostartResult.Disabled;
        }

        var path = executablePath ?? Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            _log.Error("Could not determine this executable's path, so autostart was not enabled.");
            return AutostartResult.Failed;
        }

        key.SetValue(AutostartPlan.RunValueName, AutostartPlan.BuildRunCommand(path), RegistryValueKind.String);
        _log.Info($"Autostart enabled via the Run key ({path}).");
        return AutostartResult.Enabled;
    }

    private bool IsStartupTaskEnabled()
    {
        var task = StartupTask.GetAsync(AutostartPlan.StartupTaskId).GetAwaiter().GetResult();
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    private AutostartResult SetStartupTask(bool enabled)
    {
        var task = StartupTask.GetAsync(AutostartPlan.StartupTaskId).GetAwaiter().GetResult();

        if (!enabled)
        {
            task.Disable();
            _log.Info("Autostart disabled.");
            return AutostartResult.Disabled;
        }

        var state = task.RequestEnableAsync().GetAwaiter().GetResult();

        return state switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => Report(
                AutostartResult.Enabled, "Autostart enabled."),

            // The user turned it off in Task Manager. Windows does not let the app
            // override that, and it should not: reporting it beats a settings
            // toggle that silently springs back.
            StartupTaskState.DisabledByUser => Report(
                AutostartResult.DisabledByUser,
                "Autostart is turned off for Frost in Task Manager's Startup tab; " +
                "it has to be re-enabled there."),

            StartupTaskState.DisabledByPolicy => Report(
                AutostartResult.DisabledByPolicy,
                "Group policy prevents Frost from starting with Windows."),

            _ => Report(AutostartResult.Failed, $"Autostart could not be enabled (state {state})."),
        };
    }

    private AutostartResult Report(AutostartResult result, string message)
    {
        if (result is AutostartResult.Enabled or AutostartResult.Disabled)
        {
            _log.Info(message);
        }
        else
        {
            _log.Warn(message);
        }

        return result;
    }

    /// <summary>
    /// Whether this process is running from an MSIX package.
    /// </summary>
    /// <remarks>
    /// <c>Package.Current</c> throws rather than returning null when unpackaged,
    /// which is the documented way to find out and the reason this is a
    /// try/catch rather than a null check.
    /// </remarks>
    private static bool DetectPackaged()
    {
        try
        {
            return Package.Current is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
