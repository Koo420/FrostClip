using Frost.Shared.Settings;

namespace Frost.Engine.Audio;

/// <summary>
/// Flags moments where the game audio jumps well above its recent level.
/// </summary>
/// <remarks>
/// <para><b>What this is, honestly.</b> A heuristic. A kill, a big hit, a
/// teammate shouting — these often coincide with a loudness spike, and often do
/// not. It produces false positives and misses real moments, which is why it is
/// off by default and why it only ever places a <i>bookmark</i>: a timestamp to
/// review later, costing nothing, rather than an exported clip. It is never
/// allowed to trigger a save on its own.</para>
///
/// <para><b>What it deliberately is not.</b> There is no attempt to read
/// on-screen UI or killfeeds. That needs OCR or per-game templates, costs
/// performance on every single frame, and breaks with every game patch — the
/// exact kind of fragile, expensive cleverness this application exists to avoid.
/// A bookmark on an audio spike is cheap, game-agnostic, and honest about being
/// approximate.</para>
///
/// <para><b>How it decides.</b> Short-term RMS (about 300ms — long enough to be
/// stable, short enough to catch a gunshot) compared against a slow exponential
/// moving average of that level. Three guards keep it from firing constantly:
/// the level must also clear an absolute floor, so a spike out of near-silence
/// does not count as a moment; nothing fires until the baseline has had time to
/// settle, so the first loud sound after launch is not always a mark; and marks
/// are rate-limited, so one firefight is one mark rather than fifty.</para>
///
/// <para>Runs on the audio capture thread and allocates nothing per block.</para>
/// </remarks>
public sealed class LoudnessSpikeDetector
{
    /// <summary>
    /// Level floor in dBFS below which nothing is ever a spike.
    /// </summary>
    /// <remarks>
    /// Without this, a near-silent baseline makes any sound at all look like a
    /// 40dB jump: pausing a game and un-pausing it would mark every time.
    /// </remarks>
    public const double AbsoluteFloorDb = -45.0;

    /// <summary>Reported level for digital silence, instead of negative infinity.</summary>
    public const double SilenceDb = -120.0;

    /// <summary>Short-term window length. Balances stability against catching a short transient.</summary>
    public static TimeSpan ShortTermWindow => TimeSpan.FromMilliseconds(300);

    private readonly int _channels;
    private readonly int _windowSamples;
    private readonly double _thresholdDb;
    private readonly double _minimumGapTicks;
    private readonly double _baselineAlpha;
    private readonly long _warmUpTicks;

    private double _sumOfSquares;
    private int _samplesInWindow;
    private double _baselineDb = double.NaN;
    private double _currentLevelDb = SilenceDb;
    private long _lastMarkTicks = long.MinValue;
    private long _firstTimestampTicks = -1;
    private long _marksDetected;
    private long _blocksProcessed;

    /// <param name="sampleRate">Frames per second of the audio stream.</param>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="settings">The user's autoclip settings.</param>
    public LoudnessSpikeDetector(int sampleRate, int channels, AutoclipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (sampleRate is < 8000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unsupported sample rate.");
        }

        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Unsupported channel count.");
        }

        SampleRate = sampleRate;
        _channels = channels;
        _thresholdDb = settings.LoudnessSpikeThresholdDb;
        _minimumGapTicks = settings.MinimumSecondsBetweenMarks * TimeSpan.TicksPerSecond;

        // One frame per channel-group; RMS is over all channels together, which is
        // what a listener perceives.
        _windowSamples = Math.Max(1, (int)(sampleRate * ShortTermWindow.TotalSeconds)) * channels;

        var baselineSeconds = Math.Max(settings.LoudnessBaselineSeconds, ShortTermWindow.TotalSeconds);

        // One short window's contribution to a baseline averaged over
        // baselineSeconds. Small enough that a 300ms spike barely moves the
        // baseline it is being compared against.
        _baselineAlpha = Math.Clamp(ShortTermWindow.TotalSeconds / baselineSeconds, 0.001, 0.5);

        // Nothing fires until the baseline has settled, or the first loud sound
        // after launch would always be a mark.
        _warmUpTicks = (long)(baselineSeconds * TimeSpan.TicksPerSecond);
    }

    public int SampleRate { get; }

    /// <summary>Most recent short-term level in dBFS.</summary>
    public double CurrentLevelDb => _currentLevelDb;

    /// <summary>Current rolling baseline in dBFS, or NaN before the first window completes.</summary>
    public double BaselineDb => _baselineDb;

    public long MarksDetected => _marksDetected;

    /// <summary>Short-term windows completed.</summary>
    public long WindowsProcessed => _blocksProcessed;

    /// <summary>True once enough audio has passed for marks to be possible.</summary>
    public bool IsWarmedUp =>
        _firstTimestampTicks >= 0 && !double.IsNaN(_baselineDb) &&
        _lastSeenTicks - _firstTimestampTicks >= _warmUpTicks;

    private long _lastSeenTicks;

    /// <summary>
    /// Feeds a block of interleaved float samples in [-1, 1].
    /// </summary>
    /// <param name="samples">Interleaved samples. Length need not align to the window.</param>
    /// <param name="timestampTicks">Capture time of the first sample in the block.</param>
    /// <param name="markTimestampTicks">When a mark was detected, its timestamp.</param>
    /// <returns>True when this block completed a window that is a spike.</returns>
    public bool ProcessBlock(
        ReadOnlySpan<float> samples, long timestampTicks, out long markTimestampTicks)
    {
        markTimestampTicks = 0;

        if (samples.IsEmpty)
        {
            return false;
        }

        if (_firstTimestampTicks < 0)
        {
            _firstTimestampTicks = timestampTicks;
        }

        var detected = false;
        var ticksPerSample = TimeSpan.TicksPerSecond / (double)(SampleRate * _channels);

        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            _sumOfSquares += sample * sample;
            _samplesInWindow++;

            if (_samplesInWindow < _windowSamples)
            {
                continue;
            }

            var windowEndTicks = timestampTicks + (long)(i * ticksPerSample);
            _lastSeenTicks = windowEndTicks;

            if (CompleteWindow(windowEndTicks, out var mark) && !detected)
            {
                // At most one mark per call: the rate limit means a longer block
                // cannot produce a burst anyway, and the caller wants one answer.
                detected = true;
                markTimestampTicks = mark;
            }
        }

        _lastSeenTicks = Math.Max(
            _lastSeenTicks,
            timestampTicks + (long)(samples.Length * ticksPerSample));

        return detected;
    }

    /// <summary>Clears all state, e.g. when capture restarts.</summary>
    public void Reset()
    {
        _sumOfSquares = 0;
        _samplesInWindow = 0;
        _baselineDb = double.NaN;
        _currentLevelDb = SilenceDb;
        _lastMarkTicks = long.MinValue;
        _firstTimestampTicks = -1;
        _lastSeenTicks = 0;
    }

    private bool CompleteWindow(long windowEndTicks, out long markTimestampTicks)
    {
        markTimestampTicks = 0;

        var meanSquare = _sumOfSquares / _samplesInWindow;
        _sumOfSquares = 0;
        _samplesInWindow = 0;
        _blocksProcessed++;

        var level = ToDecibels(meanSquare);
        _currentLevelDb = level;

        if (double.IsNaN(_baselineDb))
        {
            // First window: seed the baseline rather than treating it as a jump
            // from silence.
            _baselineDb = level;
            return false;
        }

        var baseline = _baselineDb;

        // Update after comparing, so a spike is measured against the level that
        // preceded it rather than one it has already dragged upward.
        _baselineDb = baseline + (_baselineAlpha * (level - baseline));

        if (windowEndTicks - _firstTimestampTicks < _warmUpTicks)
        {
            return false;
        }

        if (level < AbsoluteFloorDb)
        {
            return false;
        }

        if (level - baseline < _thresholdDb)
        {
            return false;
        }

        if (_lastMarkTicks != long.MinValue &&
            windowEndTicks - _lastMarkTicks < _minimumGapTicks)
        {
            // One firefight is one mark, not fifty.
            return false;
        }

        _lastMarkTicks = windowEndTicks;
        _marksDetected++;
        markTimestampTicks = windowEndTicks;
        return true;
    }

    /// <summary>RMS power to dBFS, with a floor instead of negative infinity.</summary>
    private static double ToDecibels(double meanSquare) =>
        meanSquare <= 1e-12 ? SilenceDb : Math.Max(SilenceDb, 10.0 * Math.Log10(meanSquare));
}
