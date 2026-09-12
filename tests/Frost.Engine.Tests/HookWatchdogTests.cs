using Frost.Engine.Hotkeys;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class HookWatchdogTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static HookWatchdog Watchdog() =>
        new(silenceThreshold: TimeSpan.FromSeconds(20),
            minimumReinstallInterval: TimeSpan.FromSeconds(30));

    [Fact]
    public void AnUninstalledHookIsNotReinstalled()
    {
        var watchdog = Watchdog();
        Assert.False(watchdog.ShouldReinstall(1000 * Second, 1000 * Second));
    }

    [Fact]
    public void AHealthyHookIsLeftAlone()
    {
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);

        for (var t = 0L; t < 600; t += 5)
        {
            watchdog.NoteHookEvent(t * Second);
            Assert.False(watchdog.ShouldReinstall(t * Second, t * Second));
        }

        Assert.Equal(0, watchdog.ReinstallCount);
    }

    [Fact]
    public void AnIdleMachineIsNotMistakenForADeadHook()
    {
        // Nobody has touched the keyboard for ten minutes. Our hook seeing
        // nothing proves nothing, and reinstalling would be pointless churn.
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);
        watchdog.NoteHookEvent(0);

        Assert.False(watchdog.ShouldReinstall(nowTicks: 600 * Second, lastSystemInputTicks: 0));
    }

    [Fact]
    public void InputThatNeverReachedTheHookMeansTheHookIsDead()
    {
        // The failure this exists to catch: Windows silently unhooked us, hotkeys
        // stop working, and nothing anywhere says so.
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);
        watchdog.NoteHookEvent(0);

        // The user has been typing right up to now, but we last saw a key at t=0.
        Assert.True(watchdog.ShouldReinstall(nowTicks: 60 * Second, lastSystemInputTicks: 59 * Second));
    }

    [Fact]
    public void ShortGapsDoNotTriggerAReinstall()
    {
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);
        watchdog.NoteHookEvent(40 * Second);

        // 10 seconds of system input we did not see: under the 20s threshold.
        Assert.False(watchdog.ShouldReinstall(nowTicks: 51 * Second, lastSystemInputTicks: 50 * Second));
    }

    [Fact]
    public void AFreshlyInstalledHookIsGivenTimeBeforeBeingJudged()
    {
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);

        // Even with damning evidence, the minimum interval has not elapsed.
        Assert.False(watchdog.ShouldReinstall(nowTicks: 25 * Second, lastSystemInputTicks: 24 * Second));
    }

    [Fact]
    public void ADeadHookDoesNotTurnIntoAReinstallLoop()
    {
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);
        watchdog.NoteHookEvent(0);

        Assert.True(watchdog.ShouldReinstall(60 * Second, 59 * Second));
        watchdog.NoteReinstalled(60 * Second);

        // Immediately after, and for the next 30 seconds, no further attempts.
        Assert.False(watchdog.ShouldReinstall(61 * Second, 60 * Second));
        Assert.False(watchdog.ShouldReinstall(89 * Second, 88 * Second));

        Assert.True(watchdog.ShouldReinstall(120 * Second, 119 * Second));
        Assert.Equal(1, watchdog.ReinstallCount);
    }

    [Fact]
    public void AReinstallThatWorksStopsFurtherAttempts()
    {
        var watchdog = Watchdog();
        watchdog.NoteInstalled(0);
        watchdog.NoteHookEvent(0);

        Assert.True(watchdog.ShouldReinstall(60 * Second, 59 * Second));
        watchdog.NoteReinstalled(60 * Second);

        // The new hook starts delivering again.
        for (var t = 61L; t < 300; t++)
        {
            watchdog.NoteHookEvent(t * Second);
            Assert.False(watchdog.ShouldReinstall(t * Second, t * Second));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonsenseThresholdsAreRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HookWatchdog(silenceThreshold: TimeSpan.FromSeconds(seconds)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HookWatchdog(minimumReinstallInterval: TimeSpan.FromSeconds(seconds)));
    }
}
