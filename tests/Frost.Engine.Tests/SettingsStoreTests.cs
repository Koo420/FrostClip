using Frost.Shared.Hotkeys;
using Frost.Shared.Settings;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "frost-settings-tests", Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_directory, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AbsentFileYieldsWorkingDefaults()
    {
        var result = SettingsStore.Load(Path_);

        Assert.False(result.WasUnreadable);
        Assert.Equal(60, result.Settings.Capture.Fps);
        Assert.Equal("Dark", result.Settings.Appearance.Theme);
        Assert.True(result.Settings.Audio.CaptureSystemAudio);
        Assert.False(result.Settings.Audio.CaptureMicrophone);
        Assert.False(result.Settings.Autoclip.DetectLoudnessSpikes);
        Assert.NotEmpty(result.Settings.ResolveHotkeys());
    }

    [Fact]
    public void AutoclipIsOffByDefault()
    {
        // The spec is explicit that the heuristics are opt-in.
        Assert.False(new FrostSettings().Autoclip.DetectLoudnessSpikes);
    }

    [Fact]
    public void RoundTripsEverythingIncludingHotkeys()
    {
        var settings = new FrostSettings
        {
            Capture = new CaptureSettings
            {
                TargetKind = 1,
                MonitorDeviceName = @"\\.\DISPLAY2",
                Fps = 144,
                CaptureCursor = true,
            },
            Encoder = new EncoderSettingsModel
            {
                Codec = 1,
                PreferredEncoderName = "NVIDIA H.264 Encoder MFT",
                BitsPerSecond = 40_000_000,
                RateControl = 1,
                KeyFrameIntervalSeconds = 1.0,
            },
            Audio = new AudioSettings { CaptureMicrophone = true, MicrophoneGain = 1.5 },
            Autoclip = new AutoclipSettings { DetectLoudnessSpikes = true, LoudnessSpikeThresholdDb = 12 },
            Storage = new StorageSettings
            {
                ClipDirectory = @"D:\Clips",
                MaxDiskUsageBytes = 50L * 1024 * 1024 * 1024,
            },
            Behaviour = new BehaviourSettings { StartWithWindows = true },
            Appearance = new AppearanceSettings { Theme = "Light", AccentColor = "#FF8800" },
            Hotkeys =
            [
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Ctrl+Shift+F5",
                    ClipSeconds = 45,
                    Label = "45s",
                },
            ],
        };

        SettingsStore.Save(settings, Path_);
        var result = SettingsStore.Load(Path_);

        Assert.True(result.IsClean, string.Join("; ", result.Corrections));
        Assert.Equal(144, result.Settings.Capture.Fps);
        Assert.Equal(@"\\.\DISPLAY2", result.Settings.Capture.MonitorDeviceName);
        Assert.Equal(1, result.Settings.Encoder.Codec);
        Assert.Equal(40_000_000, result.Settings.Encoder.BitsPerSecond);
        Assert.Equal(1.5, result.Settings.Audio.MicrophoneGain);
        Assert.True(result.Settings.Autoclip.DetectLoudnessSpikes);
        Assert.Equal(@"D:\Clips", result.Settings.Storage.ClipDirectory);
        Assert.True(result.Settings.Behaviour.StartWithWindows);
        Assert.Equal("#FF8800", result.Settings.Appearance.AccentColor);

        var hotkey = Assert.Single(result.Settings.ResolveHotkeys());
        Assert.Equal(HotkeyAction.SaveClip, hotkey.Action);
        Assert.Equal(TimeSpan.FromSeconds(45), hotkey.ClipDuration);
        Assert.Equal(
            new HotkeyBinding(0x74, HotkeyModifiers.Control | HotkeyModifiers.Shift),
            hotkey.Binding);
    }

    [Fact]
    public void SavingIsAtomicAndLeavesNoTemporaryFile()
    {
        SettingsStore.Save(new FrostSettings(), Path_);

        Assert.True(File.Exists(Path_));
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void SettingsFileIsReadableByAHuman()
    {
        // People do hand-edit these. Indented JSON with camelCase keys and the
        // binding as text is the point.
        SettingsStore.Save(new FrostSettings(), Path_);
        var json = File.ReadAllText(Path_);

        Assert.Contains("\"capture\"", json);
        Assert.Contains("\"Alt+F10\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void AFileFromAnOlderBuildGetsDefaultsForWhatIsMissing()
    {
        File.WriteAllText(Path_, """{ "version": 1, "capture": { "fps": 30 } }""");

        var result = SettingsStore.Load(Path_);

        Assert.Equal(30, result.Settings.Capture.Fps);
        Assert.Equal("Dark", result.Settings.Appearance.Theme);
        Assert.Equal(2.0, result.Settings.Encoder.KeyFrameIntervalSeconds);
        Assert.NotEmpty(result.Settings.ResolveHotkeys());
    }

    [Fact]
    public void AFileFromANewerBuildIsReadAsFarAsPossibleAndSaysSo()
    {
        File.WriteAllText(Path_, """{ "version": 99, "capture": { "fps": 120 }, "somethingNew": true }""");

        var result = SettingsStore.Load(Path_);

        Assert.Equal(120, result.Settings.Capture.Fps);
        Assert.Contains(result.Corrections, c => c.Field == "Version");
    }

    [Fact]
    public void AnUnreadableFileIsKeptAsideAndTheAppStillWorks()
    {
        // Losing the user's hotkeys because the file got truncated would be
        // unforgivable, so the broken file is preserved for hand-recovery.
        File.WriteAllText(Path_, "{ this is not json at all");

        var result = SettingsStore.Load(Path_);

        Assert.True(result.WasUnreadable);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(Path_ + ".broken"));
        Assert.Equal(60, result.Settings.Capture.Fps);
        Assert.NotEmpty(result.Settings.ResolveHotkeys());
        Assert.Contains(result.Corrections, c => c.Correction.Contains("settings.json.broken"));
    }

    [Theory]
    [InlineData(9000, 480)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void AnOutOfRangeFrameRateIsClampedRatherThanRejected(int configured, int expected)
    {
        // A hand-edited fps: 9000 should give a working recorder and a log line,
        // not a dead Engine.
        var corrections = new List<SettingsCorrection>();
        var normalised = SettingsStore.Normalise(
            new FrostSettings { Capture = new CaptureSettings { Fps = configured } }, corrections);

        Assert.Equal(expected, normalised.Capture.Fps);
        Assert.Contains(corrections, c => c.Field == "Capture.Fps");
    }

    [Fact]
    public void EveryOutOfRangeValueIsClampedAndReported()
    {
        var corrections = new List<SettingsCorrection>();

        var normalised = SettingsStore.Normalise(new FrostSettings
        {
            Capture = new CaptureSettings { TargetKind = 47 },
            Encoder = new EncoderSettingsModel
            {
                Codec = 9,
                RateControl = 9,
                KeyFrameIntervalSeconds = 0,
                BitsPerSecond = 1,
            },
            Audio = new AudioSettings { MicrophoneGain = 99 },
            Autoclip = new AutoclipSettings
            {
                LoudnessSpikeThresholdDb = 0,
                LoudnessBaselineSeconds = 9999,
                MinimumSecondsBetweenMarks = -5,
            },
            Storage = new StorageSettings { MaxDiskUsageBytes = -1, ProtectRecentDays = 9999 },
            Buffer = new BufferSettings { MaxMemoryBytes = 1, HeadroomFactor = 0.1 },
            Appearance = new AppearanceSettings { Theme = "Neon", AccentColor = "purple" },
        }, corrections);

        Assert.Equal(0, normalised.Capture.TargetKind);
        Assert.Equal(0, normalised.Encoder.Codec);
        Assert.Equal(0, normalised.Encoder.RateControl);
        Assert.Equal(2_000_000, normalised.Encoder.BitsPerSecond);
        Assert.Equal(2.0, normalised.Audio.MicrophoneGain);
        Assert.Equal(3.0, normalised.Autoclip.LoudnessSpikeThresholdDb);
        Assert.Equal(30.0, normalised.Autoclip.LoudnessBaselineSeconds);
        Assert.Equal(0.0, normalised.Autoclip.MinimumSecondsBetweenMarks);
        Assert.Equal(0, normalised.Storage.MaxDiskUsageBytes);
        Assert.Equal(365, normalised.Storage.ProtectRecentDays);
        Assert.Equal(16L * 1024 * 1024, normalised.Buffer.MaxMemoryBytes);
        Assert.Equal(1.1, normalised.Buffer.HeadroomFactor);
        Assert.Equal("Dark", normalised.Appearance.Theme);
        Assert.Equal("#3FA9F5", normalised.Appearance.AccentColor);

        // Every one of those is reported, not silently corrected.
        Assert.True(corrections.Count >= 13, $"only {corrections.Count} corrections reported");
    }

    [Fact]
    public void AnEmptyClipDirectoryFallsBackToVideosFrost()
    {
        var corrections = new List<SettingsCorrection>();
        var normalised = SettingsStore.Normalise(
            new FrostSettings { Storage = new StorageSettings { ClipDirectory = "  " } }, corrections);

        Assert.Equal(SettingsStore.DefaultClipDirectory, normalised.Storage.ClipDirectory);
    }

    [Fact]
    public void OneBadHotkeyLineDoesNotDisableTheOthers()
    {
        var settings = new FrostSettings
        {
            Hotkeys =
            [
                new HotkeyAssignmentModel { Action = (int)HotkeyAction.Bookmark, Binding = "nonsense" },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F10",
                    ClipSeconds = 30,
                },
                new HotkeyAssignmentModel
                {
                    Action = (int)HotkeyAction.SaveClip,
                    Binding = "Alt+F11",
                    ClipSeconds = -1,
                },
                new HotkeyAssignmentModel { Action = 999, Binding = "Alt+F12" },
            ],
        };

        var resolved = settings.ResolveHotkeys();

        var kept = Assert.Single(resolved);
        Assert.Equal(TimeSpan.FromSeconds(30), kept.ClipDuration);
    }

    [Fact]
    public void NoUsableHotkeyAtAllFallsBackToTheDefaultsRatherThanGoingInert()
    {
        var settings = new FrostSettings
        {
            Hotkeys = [new HotkeyAssignmentModel { Action = 0, Binding = "garbage" }],
        };

        Assert.False(settings.HasUsableHotkey());
        Assert.Equal(HotkeyAssignment.Defaults.Count, settings.ResolveHotkeys().Count);

        var corrections = new List<SettingsCorrection>();
        var normalised = SettingsStore.Normalise(settings, corrections);
        Assert.Contains(corrections, c => c.Field == "Hotkeys");
        Assert.NotEmpty(normalised.ResolveHotkeys());
    }

    [Fact]
    public void GoodSettingsAreLeftExactlyAlone()
    {
        var corrections = new List<SettingsCorrection>();
        var input = new FrostSettings
        {
            Storage = new StorageSettings { ClipDirectory = @"D:\Clips" },
        };

        var normalised = SettingsStore.Normalise(input, corrections);

        Assert.Empty(corrections);
        Assert.Equal(input, normalised);
    }

    [Fact]
    public void SavingNormalisesTheVersion()
    {
        SettingsStore.Save(new FrostSettings { Version = 0 }, Path_);
        Assert.Equal(FrostSettings.CurrentVersion, SettingsStore.Load(Path_).Settings.Version);
    }

    [Fact]
    public void NullSettingsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsStore.Save(null!, Path_));
        Assert.Throws<ArgumentNullException>(() => SettingsStore.Normalise(null!, []));
    }

    [Fact]
    public void DefaultPathsPointAtAppDataAndVideos()
    {
        Assert.Contains("Frost", SettingsStore.DefaultDirectory);
        Assert.EndsWith("settings.json", SettingsStore.DefaultPath);
        Assert.Contains("Frost", SettingsStore.DefaultClipDirectory);
    }
}
