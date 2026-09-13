using Frost.Shared.Shell;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The spec's sixth constraint — the UI must cost nothing while a game has
/// focus — expressed as tests rather than as a claim in a comment.
/// </summary>
public sealed class VisualEffectsPolicyTests
{
    [Fact]
    public void WhileAGameHasFocusTheShellSpendsNothingAtAll()
    {
        // The headline constraint. Note the poll: it is the real cost, not the
        // backdrop. A dashboard asking the Engine for status four times a second
        // wakes two processes and marshals to a UI thread forever, while the
        // user is playing and cannot see any of it.
        var budget = VisualEffectsPolicy.For(ShellActivity.BackgroundWhileGaming);

        Assert.False(budget.UseMica);
        Assert.False(budget.UseAnimations);
        Assert.False(budget.RenderThumbnails);
        Assert.Null(budget.StatusPollInterval);
        Assert.False(VisualEffectsPolicy.MayDoRecurringWork(ShellActivity.BackgroundWhileGaming));
    }

    [Fact]
    public void MicaAndAnimationsExistOnlyInTheForeground()
    {
        Assert.True(VisualEffectsPolicy.For(ShellActivity.Foreground).UseMica);
        Assert.True(VisualEffectsPolicy.For(ShellActivity.Foreground).UseAnimations);

        foreach (var activity in new[]
        {
            ShellActivity.Background,
            ShellActivity.BackgroundWhileGaming,
            ShellActivity.Hidden,
        })
        {
            var budget = VisualEffectsPolicy.For(activity);

            Assert.False(budget.UseMica, $"Mica was on while {activity}.");
            Assert.False(budget.UseAnimations, $"Animations were on while {activity}.");
        }
    }

    [Fact]
    public void TheMicaSettingIsHonouredButOnlyEverNarrowsTheBudget()
    {
        Assert.False(VisualEffectsPolicy.For(ShellActivity.Foreground, userWantsMica: false).UseMica);

        // And turning it on cannot switch it back on in a background state.
        Assert.False(
            VisualEffectsPolicy
                .For(ShellActivity.BackgroundWhileGaming, userWantsMica: true)
                .UseMica);
    }

    [Fact]
    public void AHiddenWindowDoesNothingEither()
    {
        var budget = VisualEffectsPolicy.For(ShellActivity.Hidden);

        Assert.Null(budget.StatusPollInterval);
        Assert.False(budget.RenderThumbnails);
        Assert.False(VisualEffectsPolicy.MayDoRecurringWork(ShellActivity.Hidden));
    }

    [Fact]
    public void AVisibleUnfocusedWindowPollsSlowlyRatherThanNotAtAll()
    {
        // Alt-tabbed to a browser is not gaming, and a window brought forward
        // showing a minute-old buffer length looks broken. Slow enough to be
        // free, frequent enough to be roughly true.
        var budget = VisualEffectsPolicy.For(ShellActivity.Background);

        Assert.NotNull(budget.StatusPollInterval);
        Assert.True(
            budget.StatusPollInterval > VisualEffectsPolicy.ForegroundPollInterval,
            "The background poll should be slower than the foreground one.");
    }

    [Theory]
    [InlineData(false, false, false, ShellActivity.Hidden)]
    [InlineData(false, true, false, ShellActivity.Hidden)]
    [InlineData(false, false, true, ShellActivity.Hidden)]
    [InlineData(true, true, false, ShellActivity.Foreground)]
    [InlineData(true, false, false, ShellActivity.Background)]
    [InlineData(true, false, true, ShellActivity.BackgroundWhileGaming)]
    public void TheActivityFollowsFromWhatTheWindowManagerReports(
        bool isVisible,
        bool isForeground,
        bool isGameInForeground,
        ShellActivity expected) =>
        Assert.Equal(
            expected,
            VisualEffectsPolicy.Classify(isVisible, isForeground, isGameInForeground));

    [Fact]
    public void OurOwnFocusedWindowIsNeverTreatedAsAGamePlaying()
    {
        // The two flags disagree for a moment during an alt-tab, and resolving
        // it the other way would blank the window the user just switched to.
        Assert.Equal(
            ShellActivity.Foreground,
            VisualEffectsPolicy.Classify(
                isVisible: true,
                isForeground: true,
                isGameInForeground: true));
    }

    [Fact]
    public void AMinimisedWindowIsHiddenEvenWhenTheSystemCallsItForeground()
    {
        // A minimised window can still be reported as the foreground window
        // briefly, and painting a backdrop for it is pure waste.
        Assert.Equal(
            ShellActivity.Hidden,
            VisualEffectsPolicy.Classify(
                isVisible: false,
                isForeground: true,
                isGameInForeground: false));
    }

    [Fact]
    public void ComingBackFromAGameRefreshesImmediately()
    {
        // The other half of stopping the poll: the dashboard holds whatever was
        // true when the game took focus, and waiting half a second to correct it
        // shows a stale buffer length as though it were current.
        Assert.True(VisualEffectsPolicy.ShouldRefreshOnActivation(
            ShellActivity.BackgroundWhileGaming,
            ShellActivity.Foreground));

        Assert.True(VisualEffectsPolicy.ShouldRefreshOnActivation(
            ShellActivity.Hidden,
            ShellActivity.Foreground));
    }

    [Fact]
    public void StayingInTheForegroundDoesNotForceExtraRefreshes()
    {
        Assert.False(VisualEffectsPolicy.ShouldRefreshOnActivation(
            ShellActivity.Foreground,
            ShellActivity.Foreground));

        // And losing focus is not an activation.
        Assert.False(VisualEffectsPolicy.ShouldRefreshOnActivation(
            ShellActivity.Foreground,
            ShellActivity.BackgroundWhileGaming));
    }

    [Fact]
    public void EveryActivityHasABudgetSoANewOneCannotBeSilentlyUnbounded()
    {
        foreach (var activity in Enum.GetValues<ShellActivity>())
        {
            var budget = VisualEffectsPolicy.For(activity);

            // A state that permits no recurring work must not carry a poll
            // interval, and vice versa — the two must never drift apart.
            Assert.Equal(
                VisualEffectsPolicy.MayDoRecurringWork(activity),
                budget.StatusPollInterval is not null);
        }
    }

    [Fact]
    public void AnUnknownActivityThrowsRatherThanDefaultingToSpending()
    {
        // If someone adds a state and forgets the switch, the failure has to be
        // loud. Silently falling through to the foreground budget would mean a
        // new state quietly costs a game its frame times.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VisualEffectsPolicy.For((ShellActivity)99));
    }
}
