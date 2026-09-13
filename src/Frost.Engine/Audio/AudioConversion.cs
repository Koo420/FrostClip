using System.Runtime.CompilerServices;

namespace Frost.Engine.Audio;

/// <summary>
/// The only sample-level audio maths Frost does: float→int16, gain, and mute.
/// </summary>
/// <remarks>
/// Runs on the audio capture thread, so everything here is in-place or into a
/// caller-provided buffer, and nothing allocates. Kept deliberately small —
/// Frost has no business doing DSP on someone's game audio beyond what the
/// encoder needs.
/// </remarks>
public static class AudioConversion
{
    /// <summary>
    /// Converts float samples in [-1, 1] to 16-bit PCM, clamping rather than
    /// wrapping.
    /// </summary>
    /// <remarks>
    /// Clamping matters: game audio does clip, and a wrapped sample is a loud
    /// click in the recording rather than a slightly flattened peak.
    /// </remarks>
    public static int FloatToInt16(ReadOnlySpan<float> source, Span<short> destination)
    {
        var count = Math.Min(source.Length, destination.Length);

        for (var i = 0; i < count; i++)
        {
            destination[i] = ToInt16(source[i]);
        }

        return count;
    }

    /// <summary>Converts float samples to 16-bit PCM in a byte buffer, little-endian.</summary>
    public static int FloatToInt16Bytes(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var count = Math.Min(source.Length, destination.Length / 2);

        for (var i = 0; i < count; i++)
        {
            var value = ToInt16(source[i]);
            destination[i * 2] = (byte)(value & 0xFF);
            destination[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return count * 2;
    }

    /// <summary>Applies linear gain in place, clamping to [-1, 1].</summary>
    public static void ApplyGain(Span<float> samples, double gain)
    {
        if (Math.Abs(gain - 1.0) < 1e-6)
        {
            return;
        }

        var factor = (float)gain;

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * factor, -1f, 1f);
        }
    }

    /// <summary>Silences a block, for a muted microphone.</summary>
    /// <remarks>
    /// Silence rather than dropping the block: the track has to stay in sync with
    /// the video, and a gap would shift everything after it.
    /// </remarks>
    public static void Silence(Span<float> samples) => samples.Clear();

    /// <summary>RMS level of a block in dBFS, for a level meter.</summary>
    public static double LevelDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return LoudnessSpikeDetector.SilenceDb;
        }

        double sumOfSquares = 0;

        for (var i = 0; i < samples.Length; i++)
        {
            sumOfSquares += samples[i] * (double)samples[i];
        }

        var meanSquare = sumOfSquares / samples.Length;
        return meanSquare <= 1e-12
            ? LoudnessSpikeDetector.SilenceDb
            : Math.Max(LoudnessSpikeDetector.SilenceDb, 10.0 * Math.Log10(meanSquare));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ToInt16(float sample)
    {
        // 32767 rather than 32768: scaling by 32768 makes a sample of exactly 1.0
        // overflow to -32768, which is an audible click on every clipped peak.
        // Game audio clips constantly, so this is not a corner case.
        //
        // The clamp is symmetric at ±32767 rather than using short.MinValue, so an
        // out-of-range input cannot produce a value that an in-range one never
        // could. -32768 is legal PCM, but the asymmetry is a wart with no upside.
        var scaled = sample * 32767f;
        return (short)Math.Clamp(scaled, -32767f, 32767f);
    }
}
