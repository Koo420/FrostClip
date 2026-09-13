using Frost.Shared.Ipc;
using Frost.Shared.Settings;
using Frost.Shared.Shell;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The dashboard is a projection, and the failures worth guarding against are
/// all about what it claims when the Engine is absent or broken — not layout.
/// </summary>
public sealed class DashboardModelTests
{
    private static EngineStatus Healthy(Action<EngineStatusBuilder>? tweak = null)
    {
        var builder = new EngineStatusBuilder();
        tweak?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>A mutable shim so each test states only what it cares about.</summary>
    private sealed class EngineStatusBuilder
    {
        public bool IsArmed { get; set; } = true;
        public bool IsRecordingSession { get; set; }
        public double BufferedSeconds { get; set; } = 30;
        public double MaxClipSeconds { get; set; } = 60;
        public long BufferBytes { get; set; } = 64 * 1024 * 1024;
        public int CaptureWidth { get; set; } = 1920;
        public int CaptureHeight { get; set; } = 1080;
        public int CaptureFps { get; set; } = 60;
        public string? EncoderName { get; set; } = "NVIDIA NVENC";
        public string? Codec { get; set; } = "H264";
        public long BitsPerSecond { get; set; } = 25_000_000;
        public long FramesCaptured { get; set; } = 3600;
        public long FramesDropped { get; set; }
        public long ClipsSaved { get; set; } = 4;
        public string? SessionFilePath { get; set; }
        public double UptimeSeconds { get; set; } = 300;
        public string? FaultMessage { get; set; }

        public EngineStatus Build() => new()
        {
            IsArmed = IsArmed,
            IsRecordingSession = IsRecordingSession,
            BufferedSeconds = BufferedSeconds,
            MaxClipSeconds = MaxClipSeconds,
            BufferBytes = BufferBytes,
            CaptureWidth = CaptureWidth,
            CaptureHeight = CaptureHeight,
            CaptureFps = CaptureFps,
            EncoderName = EncoderName,
            Codec = Codec,
            BitsPerSecond = BitsPerSecond,
            FramesCaptured = FramesCaptured,
            FramesDropped = FramesDropped,
            ClipsSaved = ClipsSaved,
            SessionFilePath = SessionFilePath,
            UptimeSeconds = UptimeSeconds,
            FaultMessage = FaultMessage,
        };
    }

    [Fact]
    public void WithNoEngineTheDashboardOffersNothingAndInventsNoNumbers()
    {
        var model = DashboardModel.From(null);

        Assert.Equal(EngineHealth.Disconnected, model.Health);

        // The failure this guards: a defaulted status record makes the panel read
        // "0 x 0 @ 0, 0 clips saved", which looks like a working Engine reporting
        // zeroes rather than no Engine at all.
        Assert.Null(model.BufferedSeconds);
        Assert.Null(model.MaxClipSeconds);
        Assert.Null(model.BufferFill);
        Assert.Null(model.CaptureDescription);
        Assert.Null(model.EncoderDescription);
        Assert.Null(model.ClipsSaved);
        Assert.Null(model.Uptime);
        Assert.Null(model.DropRate);

        Assert.False(model.CanSaveClip);
        Assert.False(model.CanArmBuffer);
        Assert.False(model.CanDisarmBuffer);
        Assert.False(model.CanStartSessionRecording);
        Assert.False(model.CanStopSessionRecording);
    }

    [Fact]
    public void AnArmedEngineWithFootageCanSaveAClip()
    {
        var model = DashboardModel.From(Healthy());

        Assert.Equal(EngineHealth.Armed, model.Health);
        Assert.True(model.CanSaveClip);
        Assert.True(model.CanDisarmBuffer);
        Assert.False(model.CanArmBuffer);
    }

    [Fact]
    public void AnArmedEngineWithAnEmptyBufferCannotSaveAClip()
    {
        // Pressing "save the last 30 seconds" a millisecond after arming would
        // otherwise write a clip with no frames in it.
        var model = DashboardModel.From(Healthy(s => s.BufferedSeconds = 0));

        Assert.Equal(EngineHealth.Armed, model.Health);
        Assert.False(model.CanSaveClip);
    }

    [Fact]
    public void AnIdleEngineOffersArmingAndNotClipping()
    {
        var model = DashboardModel.From(Healthy(s =>
        {
            s.IsArmed = false;
            s.BufferedSeconds = 0;
        }));

        Assert.Equal(EngineHealth.Idle, model.Health);
        Assert.True(model.CanArmBuffer);
        Assert.False(model.CanDisarmBuffer);
        Assert.False(model.CanSaveClip);
    }

    [Fact]
    public void AFaultedEngineSurfacesTheReasonAndOffersNothingThatNeedsTheEncoder()
    {
        // The real case: the fault arrives after arming, so IsArmed is still true
        // and a naive projection leaves the clip button live. Pressing it does
        // nothing, which reads as a broken app rather than a reported fault.
        var model = DashboardModel.From(Healthy(s =>
            s.FaultMessage = "No hardware encoder is available."));

        Assert.Equal(EngineHealth.Faulted, model.Health);
        Assert.Equal("No hardware encoder is available.", model.FaultMessage);
        Assert.False(model.CanSaveClip);
        Assert.False(model.CanArmBuffer);
        Assert.False(model.CanStartSessionRecording);

        // Disarming still works: it needs no encoder, and it is the one useful
        // thing to do about a faulted capture.
        Assert.True(model.CanDisarmBuffer);
    }

    [Fact]
    public void AWhitespaceFaultMessageIsNotAFault()
    {
        // Serialisation round-trips have a habit of turning absent strings into
        // empty ones, and an empty fault would grey out the whole dashboard.
        var model = DashboardModel.From(Healthy(s => s.FaultMessage = "   "));

        Assert.Equal(EngineHealth.Armed, model.Health);
        Assert.Null(model.FaultMessage);
        Assert.True(model.CanSaveClip);
    }

    [Fact]
    public void ArmedAndRecordingAreNotAlternatives()
    {
        // The spec requires the ring buffer and a full-session recording to run
        // at once off a single encode, so the dashboard must be able to say both.
        var model = DashboardModel.From(Healthy(s =>
        {
            s.IsRecordingSession = true;
            s.SessionFilePath = @"C:\Videos\Frost\session.mp4";
        }));

        Assert.Equal(EngineHealth.Armed, model.Health);
        Assert.True(model.IsRecordingSession);
        Assert.True(model.CanSaveClip);
        Assert.True(model.CanStopSessionRecording);
        Assert.False(model.CanStartSessionRecording);
        Assert.Equal(@"C:\Videos\Frost\session.mp4", model.SessionFilePath);
    }

    [Fact]
    public void BufferFillIsAFractionAndSurvivesAZeroMaximum()
    {
        Assert.Equal(0.5, DashboardModel.From(Healthy(s =>
        {
            s.BufferedSeconds = 30;
            s.MaxClipSeconds = 60;
        })).BufferFill);

        // A buffer that reports more than its own maximum (a rounding overshoot
        // at the moment of eviction) must not drive a progress bar past 100%.
        Assert.Equal(1.0, DashboardModel.From(Healthy(s =>
        {
            s.BufferedSeconds = 61;
            s.MaxClipSeconds = 60;
        })).BufferFill);

        // Not-yet-configured rather than zero-length: dividing here would be NaN.
        Assert.Null(DashboardModel.From(Healthy(s => s.MaxClipSeconds = 0)).BufferFill);
    }

    [Fact]
    public void NegativeCountersAreClampedRatherThanRendered()
    {
        var model = DashboardModel.From(Healthy(s =>
        {
            s.BufferedSeconds = -1;
            s.UptimeSeconds = -5;
        }));

        Assert.Equal(0, model.BufferedSeconds);
        Assert.Equal(TimeSpan.Zero, model.Uptime);
        Assert.False(model.CanSaveClip);
    }

    [Fact]
    public void TheDropRateIsNullBeforeAnyFrameIsCaptured()
    {
        // Zero dropped out of zero captured is not a 0% drop rate, it is no
        // measurement, and "0.0% dropped" before capture starts is a claim.
        var model = DashboardModel.From(Healthy(s =>
        {
            s.FramesCaptured = 0;
            s.FramesDropped = 0;
        }));

        Assert.Null(model.DropRate);
        Assert.False(model.IsDroppingFrames);
    }

    [Fact]
    public void ASingleDroppedFrameDoesNotRaiseAWarning()
    {
        // WGC legitimately drops a frame across a mode switch or a fullscreen
        // transition. Warning on the first one trains people to ignore warnings.
        var model = DashboardModel.From(Healthy(s =>
        {
            s.FramesCaptured = 3600;
            s.FramesDropped = 1;
        }));

        Assert.NotNull(model.DropRate);
        Assert.False(model.IsDroppingFrames);
    }

    [Fact]
    public void SustainedDroppingRaisesAWarning()
    {
        var model = DashboardModel.From(Healthy(s =>
        {
            s.FramesCaptured = 3600;
            s.FramesDropped = 400;
        }));

        Assert.True(model.IsDroppingFrames);
        Assert.Equal(400.0 / 4000.0, model.DropRate!.Value, 6);
    }

    [Fact]
    public void CaptureAndEncoderDescriptionsAreNullWhenUnknownRatherThanZeroed()
    {
        var model = DashboardModel.From(Healthy(s =>
        {
            s.CaptureWidth = 0;
            s.CaptureHeight = 0;
            s.EncoderName = null;
        }));

        Assert.Null(model.CaptureDescription);
        Assert.Null(model.EncoderDescription);
    }

    [Fact]
    public void DescriptionsReadTheWayTheStatusPanelWantsThem()
    {
        var model = DashboardModel.From(Healthy());

        Assert.Equal("1920 x 1080 @ 60", model.CaptureDescription);
        Assert.Equal("NVIDIA NVENC H264 @ 25 Mb/s", model.EncoderDescription);
    }

    [Fact]
    public void AnEncoderWithNoBitrateYetIsStillNamed()
    {
        var model = DashboardModel.From(Healthy(s =>
        {
            s.BitsPerSecond = 0;
            s.Codec = null;
        }));

        Assert.Equal("NVIDIA NVENC", model.EncoderDescription);
    }

    [Fact]
    public void QuickClipButtonsMirrorTheBoundHotkeysSoTheKeyboardAndUiAgree()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F9",
                    ClipSeconds = 15,
                },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.ToggleFullSessionRecording,
                    Binding = "Alt+F11",
                },
            ],
        };

        var options = DashboardModel.From(Healthy(s => s.BufferedSeconds = 60))
            .QuickClipOptions(settings);

        // Sorted, clip-only, and nothing from the session-recording hotkey.
        Assert.Equal([15.0, 30.0], options.Select(o => o.RequestedSeconds));
        Assert.All(options, o => Assert.True(o.IsEnabled));
        Assert.All(options, o => Assert.False(o.IsShortOfRequested));
        Assert.Equal(["15s", "30s"], options.Select(o => o.Label));
    }

    [Fact]
    public void APresetLongerThanTheBufferHoldsReportsWhatItWouldReallySave()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F12",
                    ClipSeconds = 300,
                },
            ],
        };

        var option = Assert.Single(
            DashboardModel.From(Healthy(s => s.BufferedSeconds = 11))
                .QuickClipOptions(settings));

        // Still pressable — a greyed-out button eleven seconds into a session
        // looks like a broken app rather than a buffer that is still filling —
        // but honest about the eleven seconds.
        Assert.True(option.IsEnabled);
        Assert.True(option.IsShortOfRequested);
        Assert.Equal(300, option.RequestedSeconds);
        Assert.Equal(11, option.AvailableSeconds);
    }

    [Fact]
    public void DisabledAndDurationlessHotkeysProduceNoButtons()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                    Enabled = false,
                },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F8",
                    ClipSeconds = null,
                },
            ],
        };

        Assert.Empty(DashboardModel.From(Healthy()).QuickClipOptions(settings));
    }

    [Fact]
    public void TwoHotkeysBoundToTheSameDurationProduceOneButton()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Ctrl+F10",
                    ClipSeconds = 30,
                },
            ],
        };

        Assert.Single(DashboardModel.From(Healthy()).QuickClipOptions(settings));
    }

    [Fact]
    public void QuickClipButtonsAreDisabledWhenTheEngineCannotClip()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                },
            ],
        };

        var faulted = DashboardModel.From(Healthy(s => s.FaultMessage = "Disk full."));
        Assert.All(faulted.QuickClipOptions(settings), o => Assert.False(o.IsEnabled));

        // And with no Engine at all there is nothing to clip from.
        Assert.All(
            DashboardModel.Disconnected.QuickClipOptions(settings),
            o => Assert.False(o.IsEnabled));
    }

    [Fact]
    public void ACustomLabelIsUsedForTheButtonText()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                    Label = "Clutch",
                },
            ],
        };

        Assert.Equal(
            "Clutch",
            Assert.Single(DashboardModel.From(Healthy()).QuickClipOptions(settings)).Label);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(15, "15s")]
    [InlineData(59.4, "59s")]
    [InlineData(60, "1m")]
    [InlineData(95, "1m 35s")]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(5400, "1h 30m")]
    [InlineData(-5, "0s")]
    public void DurationsReadAsPeopleWriteThem(double seconds, string expected) =>
        Assert.Equal(expected, DashboardModel.FormatDuration(seconds));

    [Fact]
    public void ANaNDurationDoesNotRenderAsNaN()
    {
        // A corrupt settings file can put one here, and "NaNs" on a button is
        // worse than a harmless zero.
        Assert.Equal("0s", DashboardModel.FormatDuration(double.NaN));
    }
}
