using Frost.Engine.Overlay;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class ToastPolicyTests
{
    private const long Millisecond = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void TheToastNeverOutlastsTheSpecsCeiling()
    {
        // The whole constraint: while a game has focus, the only UI cost allowed is
        // a static image on screen for under 1.5s.
        Assert.True(ToastPolicy.VisibleDuration < ToastPolicy.MaximumVisibleDuration);
        Assert.True(ToastPolicy.MaximumVisibleDuration <= TimeSpan.FromMilliseconds(1_500));
        Assert.Equal(ToastPolicy.VisibleDuration, new ToastPolicy().EffectiveDuration);
    }

    [Fact]
    public void AConfiguredDurationOverTheCeilingIsClampedNotRejected()
    {
        // A configuration asking for three seconds should get a compliant toast,
        // not an exception at startup.
        var policy = new ToastPolicy(visibleDuration: TimeSpan.FromSeconds(3));
        Assert.Equal(ToastPolicy.MaximumVisibleDuration, policy.EffectiveDuration);
    }

    [Fact]
    public void ShowingAToastPutsItOnScreen()
    {
        var policy = new ToastPolicy();

        Assert.Equal(ToastAction.Show, policy.Request(ToastKind.ClipSaved, 0));
        Assert.Equal(ToastKind.ClipSaved, policy.Visible);
        Assert.Equal(1, policy.ToastsShown);
    }

    [Fact]
    public void ItHidesOnceItsTimeIsUp()
    {
        var policy = new ToastPolicy(visibleDuration: TimeSpan.FromMilliseconds(1_200));
        policy.Request(ToastKind.ClipSaved, 0);

        Assert.False(policy.ShouldHide(1_199 * Millisecond));
        Assert.True(policy.ShouldHide(1_200 * Millisecond));

        policy.NoteHidden();
        Assert.Null(policy.Visible);
        Assert.False(policy.ShouldHide(10_000 * Millisecond));
    }

    [Fact]
    public void ASecondToastReplacesRatherThanStacking()
    {
        // Two toasts at once would be two windows, and a queue of them could
        // outlast the ceiling between them.
        var policy = new ToastPolicy();

        policy.Request(ToastKind.ClipSaving, 0);
        Assert.Equal(ToastAction.Replace, policy.Request(ToastKind.ClipSaved, 200 * Millisecond));

        Assert.Equal(ToastKind.ClipSaved, policy.Visible);
        Assert.Equal(1, policy.ToastsReplaced);
    }

    [Fact]
    public void TheReplacementGetsTheFullDurationFromWhenItAppeared()
    {
        var policy = new ToastPolicy(visibleDuration: TimeSpan.FromMilliseconds(1_000));

        policy.Request(ToastKind.ClipSaving, 0);
        policy.Request(ToastKind.ClipSaved, 800 * Millisecond);

        Assert.False(policy.ShouldHide(1_700 * Millisecond));
        Assert.True(policy.ShouldHide(1_800 * Millisecond));
    }

    [Fact]
    public void RequestingTheSameToastAgainJustRestartsTheTimer()
    {
        // Three clips in quick succession should not repaint anything.
        var policy = new ToastPolicy(visibleDuration: TimeSpan.FromMilliseconds(1_000));

        policy.Request(ToastKind.ClipSaved, 0);
        Assert.Equal(ToastAction.ExtendCurrent, policy.Request(ToastKind.ClipSaved, 500 * Millisecond));

        Assert.Equal(1, policy.ToastsShown);
        Assert.Equal(0, policy.ToastsReplaced);
        Assert.False(policy.ShouldHide(1_400 * Millisecond));
        Assert.True(policy.ShouldHide(1_500 * Millisecond));
    }

    [Fact]
    public void FeedbackIsTiedToTheKeypressNotTheFile()
    {
        // "Saving" goes up immediately and is superseded when the write finishes.
        // That is what makes the sub-150ms budget reachable without a disk wait.
        var policy = new ToastPolicy();

        Assert.Equal(ToastAction.Show, policy.Request(ToastKind.ClipSaving, 0));
        Assert.Equal(ToastKind.ClipSaving, policy.Visible);

        // 400ms later the file is written.
        Assert.Equal(ToastAction.Replace, policy.Request(ToastKind.ClipSaved, 400 * Millisecond));
        Assert.Equal(ToastKind.ClipSaved, policy.Visible);
    }

    [Fact]
    public void DisablingToastsCreatesNothingAtAll()
    {
        var policy = new ToastPolicy(enabled: false);

        Assert.Equal(ToastAction.Ignore, policy.Request(ToastKind.ClipSaved, 0));
        Assert.Null(policy.Visible);
        Assert.Equal(0, policy.ToastsShown);
        Assert.Equal(1, policy.RequestsSuppressed);
        Assert.False(policy.ShouldHide(10_000 * Millisecond));
    }

    [Fact]
    public void TheOverlayCanWaitExactlyAsLongAsItNeeds()
    {
        // So an idle Engine does no work for the overlay at all, rather than
        // polling a timer.
        var policy = new ToastPolicy(visibleDuration: TimeSpan.FromMilliseconds(1_000));

        Assert.Null(policy.TicksUntilHide(0));

        policy.Request(ToastKind.ClipSaved, 0);
        Assert.Equal(1_000 * Millisecond, policy.TicksUntilHide(0));
        Assert.Equal(400 * Millisecond, policy.TicksUntilHide(600 * Millisecond));
        Assert.Equal(0, policy.TicksUntilHide(2_000 * Millisecond));
    }

    [Theory]
    [InlineData(true, false, ToastKind.ClipSaved)]
    [InlineData(false, false, ToastKind.ClipFailed)]
    [InlineData(false, true, ToastKind.NothingToClip)]
    [InlineData(true, true, ToastKind.NothingToClip)]
    public void ClipOutcomesMapToTheRightToast(bool succeeded, bool nothingBuffered, ToastKind expected) =>
        Assert.Equal(expected, ToastPolicy.ForClipResult(succeeded, nothingBuffered));

    [Fact]
    public void EveryKindHasTextSoTheWholeSetCanBePreRendered()
    {
        // Pre-rendering the closed set at startup is what keeps showing a toast a
        // window-position call rather than a paint.
        Assert.NotEmpty(ToastPolicy.AllKinds);

        foreach (var kind in ToastPolicy.AllKinds)
        {
            var text = ToastPolicy.TextFor(kind);

            Assert.False(string.IsNullOrWhiteSpace(text));

            // Short enough to read in passing, which is all the time it gets.
            Assert.True(text.Length <= 24, $"'{text}' is too long for a glance");
        }
    }

    [Fact]
    public void AnUnknownKindIsRejectedRatherThanRenderedBlank() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ToastPolicy.TextFor((ToastKind)99));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveDurationsAreRejected(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToastPolicy(visibleDuration: TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void ALongRunOfClipsNeverLeavesAToastUpBeyondTheCeiling()
    {
        // The property that matters: however the user spams hotkeys, nothing is on
        // screen for longer than the ceiling after the last request.
        var policy = new ToastPolicy();
        var clock = 0L;

        for (var i = 0; i < 200; i++)
        {
            policy.Request(i % 2 == 0 ? ToastKind.ClipSaving : ToastKind.ClipSaved, clock);
            clock += 50 * Millisecond;
        }

        var lastRequest = clock - (50 * Millisecond);
        Assert.False(policy.ShouldHide(lastRequest));
        Assert.True(policy.ShouldHide(lastRequest + ToastPolicy.MaximumVisibleDuration.Ticks));
    }
}
