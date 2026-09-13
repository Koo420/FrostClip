using Frost.Engine.Clips;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Frost.Engine.Hotkeys;
using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class HotkeyBindingTests
{
    [Theory]
    [InlineData(VirtualKeys.F10, HotkeyModifiers.None, "F10")]
    [InlineData(VirtualKeys.F10, HotkeyModifiers.Alt, "Alt+F10")]
    [InlineData(VirtualKeys.F10, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+F10")]
    [InlineData((int)'K', HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Ctrl+Alt+Shift+K")]
    [InlineData(0x20, HotkeyModifiers.Windows, "Win+Space")]
    public void RendersInAStableOrder(int key, HotkeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, new HotkeyBinding(key, modifiers).ToString());

    [Fact]
    public void ModifierOrderDoesNotChangeTheRendering()
    {
        var a = new HotkeyBinding(VirtualKeys.F9, HotkeyModifiers.Shift | HotkeyModifiers.Control);
        var b = new HotkeyBinding(VirtualKeys.F9, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(a, b);
    }

    [Fact]
    public void UnboundRendersPlainly() => Assert.Equal("(unbound)", HotkeyBinding.None.ToString());

    [Theory]
    [InlineData("Alt+F10")]
    [InlineData("ctrl+shift+f10")]
    [InlineData("CTRL + ALT + K")]
    [InlineData("Win+Space")]
    [InlineData("F9")]
    [InlineData("Num5")]
    public void RoundTripsThroughParsing(string text)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding));
        Assert.True(HotkeyBinding.TryParse(binding.ToString(), out var again));
        Assert.Equal(binding, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("F9+F10")]
    [InlineData("NotAKey")]
    public void NonsenseDoesNotParse(string? text) =>
        Assert.False(HotkeyBinding.TryParse(text, out _));

    [Fact]
    public void AnUnnamedKeyStillSurvivesASaveAndLoad()
    {
        // An exotic key must not be silently lost by a settings round trip.
        var binding = new HotkeyBinding(0x07, HotkeyModifiers.Alt);
        var text = binding.ToString();

        Assert.Equal("Alt+VK_07", text);
        Assert.True(HotkeyBinding.TryParse(text, out var parsed));
        Assert.Equal(binding, parsed);
    }
}

public sealed class HotkeyAssignmentTests
{
    [Theory]
    [InlineData(15, "15s")]
    [InlineData(30, "30s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m30s")]
    [InlineData(300, "5m")]
    public void DurationsFormatTheWayAUserWouldWriteThem(int seconds, string expected) =>
        Assert.Equal(expected, HotkeyAssignment.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void LabelDefaultsToTheDuration()
    {
        var assignment = new HotkeyAssignment
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F9, HotkeyModifiers.Alt),
            ClipDuration = TimeSpan.FromSeconds(30),
        };

        Assert.Equal("30s", assignment.EffectiveLabel);
        Assert.Equal("custom", (assignment with { Label = "custom" }).EffectiveLabel);
    }

    [Fact]
    public void DefaultsCoverThePresetsTheSpecAsksFor()
    {
        var clips = HotkeyAssignment.Defaults
            .Where(a => a.Action == HotkeyAction.SaveClip)
            .Select(a => a.ClipDuration)
            .ToList();

        Assert.Contains(TimeSpan.FromSeconds(15), clips);
        Assert.Contains(TimeSpan.FromSeconds(30), clips);
        Assert.Contains(TimeSpan.FromSeconds(60), clips);

        Assert.Contains(HotkeyAssignment.Defaults, a => a.Action == HotkeyAction.ToggleFullSessionRecording);
        Assert.Contains(HotkeyAssignment.Defaults, a => a.Action == HotkeyAction.Bookmark);

        foreach (var assignment in HotkeyAssignment.Defaults)
        {
            assignment.Validate();
        }
    }

    [Fact]
    public void DefaultBindingsDoNotCollide()
    {
        var bindings = HotkeyAssignment.Defaults.Select(a => a.Binding).ToList();
        Assert.Equal(bindings.Count, bindings.Distinct().Count());
    }

    [Fact]
    public void AnUnboundAssignmentIsRejected() =>
        Assert.Throws<ArgumentException>(() => new HotkeyAssignment
        {
            Action = HotkeyAction.Bookmark,
            Binding = HotkeyBinding.None,
        }.Validate());

    [Fact]
    public void ASaveClipHotkeyWithoutADurationIsRejected() =>
        Assert.Throws<ArgumentException>(() => new HotkeyAssignment
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F9),
        }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3600)]
    public void ImpossibleClipDurationsAreRejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HotkeyAssignment
        {
            Action = HotkeyAction.SaveClip,
            Binding = new HotkeyBinding(VirtualKeys.F9),
            ClipDuration = TimeSpan.FromSeconds(seconds),
        }.Validate());
}

public sealed class HotkeyRouterTests
{
    private static HotkeyAssignment Clip(int key, int seconds) => new()
    {
        Action = HotkeyAction.SaveClip,
        Binding = new HotkeyBinding(key, HotkeyModifiers.Alt),
        ClipDuration = TimeSpan.FromSeconds(seconds),
    };

    [Fact]
    public void ThreeDurationsOnThreeKeysAllFireIndependently()
    {
        // Phase 3's last requirement, stated directly.
        var router = new HotkeyRouter([Clip(VirtualKeys.F9, 15), Clip(VirtualKeys.F10, 30), Clip(VirtualKeys.F11, 60)]);

        var fired = new List<TimeSpan>();
        router.Fired += e => fired.Add(e.Assignment.ClipDuration!.Value);

        foreach (var key in new[] { VirtualKeys.F10, VirtualKeys.F9, VirtualKeys.F11 })
        {
            router.OnKeyDown(key, HotkeyModifiers.Alt);
            router.OnKeyUp(key);
        }

        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)],
            fired);
    }

    [Fact]
    public void TheRingOnlyNeedsSizingForTheLongestPreset()
    {
        var router = new HotkeyRouter(
        [
            Clip(VirtualKeys.F9, 15),
            Clip(VirtualKeys.F10, 30),
            Clip(VirtualKeys.F11, 300),
            new HotkeyAssignment
            {
                Action = HotkeyAction.Bookmark,
                Binding = new HotkeyBinding(VirtualKeys.F1, HotkeyModifiers.Alt),
            },
        ]);

        Assert.Equal(TimeSpan.FromSeconds(300), router.LongestClipDuration);
    }

    [Fact]
    public void NoClipHotkeysMeansNoRingBufferNeeded()
    {
        var router = new HotkeyRouter(
        [
            new HotkeyAssignment
            {
                Action = HotkeyAction.Bookmark,
                Binding = new HotkeyBinding(VirtualKeys.F1),
            },
        ]);

        Assert.Equal(TimeSpan.Zero, router.LongestClipDuration);
    }

    [Fact]
    public void HoldingTheKeyFiresExactlyOnce()
    {
        // Without edge detection, holding Alt+F10 would try to save thirty clips
        // a second.
        var router = new HotkeyRouter([Clip(VirtualKeys.F10, 30)]);
        var count = 0;
        router.Fired += _ => count++;

        for (var i = 0; i < 50; i++)
        {
            router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt);
        }

        Assert.Equal(1, count);

        router.OnKeyUp(VirtualKeys.F10);
        router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        // Alt+F10 must not fire on Ctrl+Alt+F10: the user bound a specific combo.
        var router = new HotkeyRouter([Clip(VirtualKeys.F10, 30)]);
        var count = 0;
        router.Fired += _ => count++;

        Assert.Equal(0, router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.None));
        Assert.Equal(0, router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt | HotkeyModifiers.Control));
        Assert.Equal(1, router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt));
        Assert.Equal(1, count);
    }

    [Fact]
    public void DisabledAssignmentsAreNotServed()
    {
        var router = new HotkeyRouter([Clip(VirtualKeys.F10, 30) with { Enabled = false }]);

        Assert.Equal(0, router.Count);
        Assert.Equal(0, router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt));
    }

    [Fact]
    public void UnboundAssignmentsAreSkippedRatherThanThrowing()
    {
        var router = new HotkeyRouter(
        [
            new HotkeyAssignment { Action = HotkeyAction.Bookmark, Binding = HotkeyBinding.None },
            Clip(VirtualKeys.F10, 30),
        ]);

        Assert.Equal(1, router.Count);
    }

    [Fact]
    public void OneKeyBoundTwiceFiresBothAndIsReportedAsAConflict()
    {
        var router = new HotkeyRouter([Clip(VirtualKeys.F10, 15), Clip(VirtualKeys.F10, 30)]);

        Assert.Equal(2, router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt));

        var conflict = Assert.Single(router.Conflicts());
        Assert.Equal(VirtualKeys.F10, conflict.VirtualKey);
    }

    [Fact]
    public void NonConflictingBindingsReportNoConflicts() =>
        Assert.Empty(new HotkeyRouter(HotkeyAssignment.Defaults).Conflicts());

    [Fact]
    public void ResettingHeldStateReArmsEverything()
    {
        // If a key-up is missed - focus lost mid-press, hook re-installed - a
        // binding must not stay latched down forever.
        var router = new HotkeyRouter([Clip(VirtualKeys.F10, 30)]);
        var count = 0;
        router.Fired += _ => count++;

        router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt);
        Assert.Equal(1, count);

        router.ResetHeldState();
        router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt);
        Assert.Equal(2, count);
    }

    [Fact]
    public void MatchingDoesNotAllocate()
    {
        // This runs inside the low-level keyboard hook, on every keystroke the
        // user makes, including the ones they are aiming with.
        var router = new HotkeyRouter(HotkeyAssignment.Defaults);

        AllocationAssert.NoPerIterationAllocation(_ =>
        {
            router.OnKeyDown('W', HotkeyModifiers.None);
            router.OnKeyUp('W');
            router.OnKeyDown(VirtualKeys.F10, HotkeyModifiers.Alt | HotkeyModifiers.Control);
        });
    }

    [Fact]
    public void ThreePresetsProduceThreeClipsOfThreeLengths()
    {
        // End to end over the real ring buffer and clip service: one encode, one
        // buffer, three hotkeys, three different clip lengths.
        const int fps = 60;
        var frameTicks = TimeSpan.TicksPerSecond / fps;

        var ring = new EncodedSampleRing(new RingBufferOptions
        {
            MaxTrailingDuration = TimeSpan.FromSeconds(60),
            BitsPerSecond = 12_400_000,
            Fps = fps,
        });

        var keyFrame = new byte[40_000];
        var interFrame = new byte[8_000];
        for (var i = 0; i < fps * 90; i++)
        {
            var isKey = i % 120 == 0;
            ring.TryWrite(isKey ? keyFrame : interFrame, i * frameTicks, frameTicks, isKey);
        }

        var writer = new FakeClipWriter();
        var directory = Path.Combine(Path.GetTempPath(), "frost-presets", Guid.NewGuid().ToString("N"));
        using var clips = new ClipService(ring, writer, directory, NullEngineLog.Instance, _ => false);

        var router = new HotkeyRouter(
            [Clip(VirtualKeys.F9, 15), Clip(VirtualKeys.F10, 30), Clip(VirtualKeys.F11, 60)]);

        router.Fired += e => clips.Request(new ClipRequest(
            e.Assignment.ClipDuration!.Value, e.Assignment.EffectiveLabel, "Test Game"));

        foreach (var key in new[] { VirtualKeys.F9, VirtualKeys.F10, VirtualKeys.F11 })
        {
            router.OnKeyDown(key, HotkeyModifiers.Alt);
            router.OnKeyUp(key);
        }

        Assert.True(clips.WaitForIdle(TimeSpan.FromSeconds(60)));
        Assert.Equal(3, writer.Written.Count);

        TimeSpan DurationFor(string label) =>
            writer.Written.Single(w => w.Path.Contains($"({label})", StringComparison.Ordinal)).Duration;

        // Each clip covers at least what was asked for, and at most one keyframe
        // interval (2s here) more.
        foreach (var (label, seconds) in new[] { ("15s", 15), ("30s", 30), ("1m", 60) })
        {
            var actual = DurationFor(label);
            Assert.True(actual >= TimeSpan.FromSeconds(seconds), $"{label} covered only {actual}");
            Assert.True(actual < TimeSpan.FromSeconds(seconds + 2.1), $"{label} covered {actual}");
        }
    }
}
