using Frost.Engine.Diagnostics;
using Frost.Engine.Recording;
using Frost.Shared.Clips;
using Frost.Shared.Settings;

namespace Frost.Engine.Audio;

/// <summary>
/// Turns detected loudness spikes into bookmarks in the session recording.
/// </summary>
/// <remarks>
/// <para>Deliberately the whole of the automatic path: a spike becomes a
/// <i>bookmark</i> and nothing else. It never saves a clip, never starts a
/// recording, and never touches the ring buffer. A heuristic that is wrong
/// sometimes can be allowed to add a timestamp the user might ignore; it cannot
/// be allowed to fill someone's drive with clips of nothing.</para>
///
/// <para>When the feature is off — which it is by default — this is a single
/// field check per audio block and no detector is constructed at all.</para>
/// </remarks>
public sealed class AutoclipBookmarker
{
    private readonly IBookmarkTarget _target;
    private readonly IEngineLog _log;
    private readonly LoudnessSpikeDetector? _detector;

    private long _marksPlaced;
    private long _marksDeclined;

    /// <param name="settings">
    /// When <see cref="AutoclipSettings.DetectLoudnessSpikes"/> is false, no
    /// detector is created and <see cref="ProcessAudio"/> does nothing.
    /// </param>
    public AutoclipBookmarker(
        AutoclipSettings settings,
        int sampleRate,
        int channels,
        IBookmarkTarget target,
        IEngineLog log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);

        _target = target;
        _log = log;

        if (settings.DetectLoudnessSpikes)
        {
            _detector = new LoudnessSpikeDetector(sampleRate, channels, settings);
            log.Info(
                $"Autoclip loudness detection on: {settings.LoudnessSpikeThresholdDb:F0}dB above a " +
                $"{settings.LoudnessBaselineSeconds:F0}s baseline, at most one mark every " +
                $"{settings.MinimumSecondsBetweenMarks:F0}s. Best-effort by nature.");
        }
    }

    /// <summary>Whether the user opted in.</summary>
    public bool IsEnabled => _detector is not null;

    /// <summary>Bookmarks actually placed.</summary>
    public long MarksPlaced => Interlocked.Read(ref _marksPlaced);

    /// <summary>
    /// Spikes detected that could not be marked, because no session recording was
    /// running. Expected and harmless: the detector runs whenever audio does.
    /// </summary>
    public long MarksDeclined => Interlocked.Read(ref _marksDeclined);

    /// <summary>Current short-term level, for a level meter in the Shell.</summary>
    public double CurrentLevelDb => _detector?.CurrentLevelDb ?? LoudnessSpikeDetector.SilenceDb;

    /// <summary>Current rolling baseline, for the Shell.</summary>
    public double BaselineDb => _detector?.BaselineDb ?? double.NaN;

    /// <summary>
    /// Feeds audio. Called from the audio capture thread; allocation-free.
    /// </summary>
    public void ProcessAudio(ReadOnlySpan<float> samples, long timestampTicks)
    {
        if (_detector is null)
        {
            return;
        }

        if (!_detector.ProcessBlock(samples, timestampTicks, out _))
        {
            return;
        }

        if (_target.TryAddBookmark(BookmarkSource.AudioLoudnessSpike, label: null, out _))
        {
            Interlocked.Increment(ref _marksPlaced);
        }
        else
        {
            // Nothing to mark — no session recording is running. Not worth a log
            // line per occurrence; the counter is enough.
            Interlocked.Increment(ref _marksDeclined);
        }
    }

    /// <summary>Clears detector state, e.g. when capture restarts.</summary>
    public void Reset() => _detector?.Reset();

    /// <summary>Summary for the log when a session ends.</summary>
    public string Describe() => _detector is null
        ? "autoclip loudness detection off"
        : $"autoclip: {MarksPlaced} bookmark(s) placed, {MarksDeclined} spike(s) with nothing to mark";
}
