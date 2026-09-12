using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frost.Shared.Settings;

/// <summary>Source-generated JSON for the settings file. See the note in FrostClipJsonContext.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FrostSettings))]
public partial class FrostSettingsJsonContext : JsonSerializerContext;

/// <summary>Something in the settings file that had to be corrected.</summary>
/// <param name="Field">Dotted path, e.g. <c>Capture.Fps</c>.</param>
/// <param name="Problem">What was wrong.</param>
/// <param name="Correction">What it was changed to.</param>
public readonly record struct SettingsCorrection(string Field, string Problem, string Correction)
{
    public override string ToString() => $"{Field}: {Problem} — using {Correction}.";
}

/// <summary>Reads and writes the settings file.</summary>
/// <remarks>
/// <para>Two rules shape this. First, <b>a bad settings file must never stop the
/// Engine from recording</b>: values out of range are clamped and reported
/// rather than rejected, an unparseable file falls back to defaults and is kept
/// aside instead of deleted, and a single bad hotkey line does not disable the
/// others. The user's recourse for a truly broken file is a working app plus a
/// log line, not a process that will not start.</para>
///
/// <para>Second, writes are atomic: a temp file plus a replace, so a crash or a
/// power loss mid-save cannot leave a truncated settings file — which would then
/// be read back as "all defaults" and quietly lose the user's hotkeys.</para>
/// </remarks>
public static class SettingsStore
{
    public const string FileName = "settings.json";

    /// <summary>
    /// Options used for the settings file specifically.
    /// </summary>
    /// <remarks>
    /// The relaxed encoder is here for one reason: the default encoder escapes
    /// <c>+</c> as <c>\u002B</c>, which turns every hotkey in the file into
    /// <c>"Alt\u002BF10"</c>. This file is meant to be readable and editable by
    /// hand, so that is not acceptable. "Unsafe" refers to embedding JSON in
    /// HTML; this is a local file that is never served anywhere.
    /// </remarks>
    private static readonly FrostSettingsJsonContext WriteContext = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>%AppData%\Frost — roaming, so settings follow a domain profile.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FrostInfo.AppDataFolderName);

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    /// <summary>Default clip directory: Videos\Frost.</summary>
    public static string DefaultClipDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        FrostInfo.ProductName);

    /// <summary>
    /// Loads settings, applying defaults for anything absent and clamping
    /// anything out of range.
    /// </summary>
    public static SettingsLoadResult Load(string? path = null)
    {
        path ??= DefaultPath;
        var corrections = new List<SettingsCorrection>();

        if (!File.Exists(path))
        {
            return new SettingsLoadResult(Normalise(new FrostSettings(), corrections), corrections, false, null);
        }

        FrostSettings? parsed;
        string? parseError = null;

        try
        {
            var json = File.ReadAllText(path);
            parsed = JsonSerializer.Deserialize(json, FrostSettingsJsonContext.Default.FrostSettings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            parsed = null;
            parseError = ex.Message;
        }

        if (parsed is null)
        {
            // Keep the unreadable file: it may contain hotkeys the user spent
            // time on, and they can recover them by hand.
            QuarantineBrokenFile(path);

            corrections.Add(new SettingsCorrection(
                "(file)",
                parseError is null ? "could not be read" : $"could not be read ({parseError})",
                "defaults; the old file was kept as settings.json.broken"));

            return new SettingsLoadResult(
                Normalise(new FrostSettings(), corrections), corrections, true, parseError);
        }

        return new SettingsLoadResult(Normalise(Migrate(parsed, corrections), corrections), corrections, false, null);
    }

    /// <summary>Writes settings atomically.</summary>
    public static void Save(FrostSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            settings with { Version = FrostSettings.CurrentVersion },
            WriteContext.FrostSettings);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);

        // Replace rather than delete-then-move: the window where no settings file
        // exists is what turns a crash into lost configuration.
        File.Move(temporary, path, overwrite: true);
    }

    private static FrostSettings Migrate(FrostSettings settings, List<SettingsCorrection> corrections)
    {
        if (settings.Version == FrostSettings.CurrentVersion)
        {
            return settings;
        }

        if (settings.Version > FrostSettings.CurrentVersion)
        {
            // Written by a newer build. Unknown fields were already dropped by
            // the deserialiser; say so rather than pretending nothing happened.
            corrections.Add(new SettingsCorrection(
                "Version",
                $"file is version {settings.Version}, newer than this build's {FrostSettings.CurrentVersion}",
                "reading what is recognisable"));
        }

        return settings with { Version = FrostSettings.CurrentVersion };
    }

    /// <summary>
    /// Clamps everything into range, recording each correction.
    /// </summary>
    /// <remarks>
    /// Clamping rather than rejecting: a hand-edited <c>fps: 9000</c> should give
    /// the user a working recorder at 480fps and a log line, not a dead Engine.
    /// </remarks>
    public static FrostSettings Normalise(FrostSettings settings, List<SettingsCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(corrections);

        // Every section is coalesced because System.Text.Json's source generator
        // does not run property initialisers: a property absent from the file
        // comes back as null, not as its default. A settings file written by an
        // older build is missing whole sections, so without this the Engine
        // would throw on startup for the most ordinary upgrade there is.
        settings = settings with
        {
            Capture = settings.Capture ?? new CaptureSettings(),
            Encoder = settings.Encoder ?? new EncoderSettingsModel(),
            Audio = settings.Audio ?? new AudioSettings(),
            Autoclip = settings.Autoclip ?? new AutoclipSettings(),
            Storage = settings.Storage ?? new StorageSettings(),
            Buffer = settings.Buffer ?? new BufferSettings(),
            Behaviour = settings.Behaviour ?? new BehaviourSettings(),
            Appearance = settings.Appearance ?? new AppearanceSettings(),
            Hotkeys = settings.Hotkeys ?? HotkeyAssignment.Defaults.Select(HotkeyAssignmentModel.From).ToList(),
        };

        var capture = settings.Capture;
        if (capture.Fps is < 1 or > 480)
        {
            var clamped = Math.Clamp(capture.Fps, 1, 480);
            corrections.Add(new SettingsCorrection("Capture.Fps", $"{capture.Fps} is out of range", $"{clamped}"));
            capture = capture with { Fps = clamped };
        }

        if (capture.TargetKind is < 0 or > 2)
        {
            corrections.Add(new SettingsCorrection(
                "Capture.TargetKind", $"{capture.TargetKind} is not a known target", "primary monitor"));
            capture = capture with { TargetKind = 0 };
        }

        var encoder = settings.Encoder;
        if (encoder.Codec is < 0 or > 2)
        {
            corrections.Add(new SettingsCorrection(
                "Encoder.Codec", $"{encoder.Codec} is not a known codec", "H.264"));
            encoder = encoder with { Codec = 0 };
        }

        if (encoder.RateControl is < 0 or > 2)
        {
            corrections.Add(new SettingsCorrection(
                "Encoder.RateControl", $"{encoder.RateControl} is not a known mode", "CBR"));
            encoder = encoder with { RateControl = 0 };
        }

        if (encoder.KeyFrameIntervalSeconds is <= 0 or > 30)
        {
            var clamped = Math.Clamp(encoder.KeyFrameIntervalSeconds, 0.5, 30);
            corrections.Add(new SettingsCorrection(
                "Encoder.KeyFrameIntervalSeconds",
                $"{encoder.KeyFrameIntervalSeconds} is out of range", $"{clamped}"));
            encoder = encoder with { KeyFrameIntervalSeconds = clamped };
        }

        if (encoder.BitsPerSecond is { } bitrate && bitrate is < 2_000_000 or > 150_000_000)
        {
            var clamped = Math.Clamp(bitrate, 2_000_000, 150_000_000);
            corrections.Add(new SettingsCorrection(
                "Encoder.BitsPerSecond", $"{bitrate} is out of range", $"{clamped}"));
            encoder = encoder with { BitsPerSecond = clamped };
        }

        var audio = settings.Audio;
        if (audio.MicrophoneGain is < 0 or > 2)
        {
            var clamped = Math.Clamp(audio.MicrophoneGain, 0, 2);
            corrections.Add(new SettingsCorrection(
                "Audio.MicrophoneGain", $"{audio.MicrophoneGain} is out of range", $"{clamped}"));
            audio = audio with { MicrophoneGain = clamped };
        }

        var autoclip = settings.Autoclip;
        if (autoclip.LoudnessSpikeThresholdDb is < 3 or > 40)
        {
            var clamped = Math.Clamp(autoclip.LoudnessSpikeThresholdDb, 3, 40);
            corrections.Add(new SettingsCorrection(
                "Autoclip.LoudnessSpikeThresholdDb",
                $"{autoclip.LoudnessSpikeThresholdDb} is out of range", $"{clamped}"));
            autoclip = autoclip with { LoudnessSpikeThresholdDb = clamped };
        }

        if (autoclip.LoudnessBaselineSeconds is < 0.5 or > 30)
        {
            var clamped = Math.Clamp(autoclip.LoudnessBaselineSeconds, 0.5, 30);
            corrections.Add(new SettingsCorrection(
                "Autoclip.LoudnessBaselineSeconds",
                $"{autoclip.LoudnessBaselineSeconds} is out of range", $"{clamped}"));
            autoclip = autoclip with { LoudnessBaselineSeconds = clamped };
        }

        if (autoclip.MinimumSecondsBetweenMarks is < 0 or > 600)
        {
            var clamped = Math.Clamp(autoclip.MinimumSecondsBetweenMarks, 0, 600);
            corrections.Add(new SettingsCorrection(
                "Autoclip.MinimumSecondsBetweenMarks",
                $"{autoclip.MinimumSecondsBetweenMarks} is out of range", $"{clamped}"));
            autoclip = autoclip with { MinimumSecondsBetweenMarks = clamped };
        }

        var storage = settings.Storage;
        if (string.IsNullOrWhiteSpace(storage.ClipDirectory))
        {
            storage = storage with { ClipDirectory = DefaultClipDirectory };
        }

        if (storage.MaxDiskUsageBytes < 0)
        {
            corrections.Add(new SettingsCorrection(
                "Storage.MaxDiskUsageBytes", "is negative", "unlimited"));
            storage = storage with { MaxDiskUsageBytes = 0 };
        }

        if (storage.ProtectRecentDays is < 0 or > 365)
        {
            var clamped = Math.Clamp(storage.ProtectRecentDays, 0, 365);
            corrections.Add(new SettingsCorrection(
                "Storage.ProtectRecentDays", $"{storage.ProtectRecentDays} is out of range", $"{clamped}"));
            storage = storage with { ProtectRecentDays = clamped };
        }

        var buffer = settings.Buffer;
        if (buffer.MaxMemoryBytes < 16L * 1024 * 1024)
        {
            corrections.Add(new SettingsCorrection(
                "Buffer.MaxMemoryBytes", $"{buffer.MaxMemoryBytes} is too small to hold any clip", "16MB"));
            buffer = buffer with { MaxMemoryBytes = 16L * 1024 * 1024 };
        }

        if (buffer.HeadroomFactor is < 1.1 or > 4.0)
        {
            var clamped = Math.Clamp(buffer.HeadroomFactor, 1.1, 4.0);
            corrections.Add(new SettingsCorrection(
                "Buffer.HeadroomFactor", $"{buffer.HeadroomFactor} is out of range", $"{clamped}"));
            buffer = buffer with { HeadroomFactor = clamped };
        }

        var appearance = settings.Appearance;
        if (appearance.Theme is not ("Dark" or "Light" or "System"))
        {
            corrections.Add(new SettingsCorrection(
                "Appearance.Theme", $"'{appearance.Theme}' is not a known theme", "Dark"));
            appearance = appearance with { Theme = "Dark" };
        }

        if (!IsHexColor(appearance.AccentColor))
        {
            corrections.Add(new SettingsCorrection(
                "Appearance.AccentColor", $"'{appearance.AccentColor}' is not a #RRGGBB colour", "#3FA9F5"));
            appearance = appearance with { AccentColor = "#3FA9F5" };
        }

        var hotkeys = settings.Hotkeys;
        if (hotkeys.Count == 0 || !settings.HasUsableHotkey())
        {
            corrections.Add(new SettingsCorrection(
                "Hotkeys", "no usable hotkey was configured", "the default bindings"));
            hotkeys = HotkeyAssignment.Defaults.Select(HotkeyAssignmentModel.From).ToList();
        }

        return settings with
        {
            Version = FrostSettings.CurrentVersion,
            Capture = capture,
            Encoder = encoder,
            Audio = audio,
            Autoclip = autoclip,
            Storage = storage,
            Buffer = buffer,
            Appearance = appearance,
            Hotkeys = hotkeys,
        };
    }

    private static void QuarantineBrokenFile(string path)
    {
        try
        {
            File.Move(path, path + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to be done about it, and it must not stop the load.
        }
    }

    private static bool IsHexColor(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Outcome of a settings load.</summary>
/// <param name="Settings">Usable settings, always — defaults where needed.</param>
/// <param name="Corrections">Everything that had to be fixed up.</param>
/// <param name="WasUnreadable">True when the file could not be parsed at all.</param>
/// <param name="Error">Parser message, when there was one.</param>
public sealed record SettingsLoadResult(
    FrostSettings Settings,
    IReadOnlyList<SettingsCorrection> Corrections,
    bool WasUnreadable,
    string? Error)
{
    public bool IsClean => Corrections.Count == 0 && !WasUnreadable;
}
