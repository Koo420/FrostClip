namespace Frost.Engine.Audio;

/// <summary>
/// Live microphone state: muted or not, and at what gain.
/// </summary>
/// <remarks>
/// <para>A tiny shared object rather than settings, because muting has to take
/// effect on the next audio block, not on the next capture restart. The hotkey
/// thread flips the flag, the capture thread reads it per block, and neither
/// waits for the other.</para>
///
/// <para>Muting silences the samples rather than dropping the block: the track
/// has to stay the same length as the video, and a gap would shift everything
/// after it.</para>
/// </remarks>
public sealed class MicrophoneState
{
    private int _muted;
    private long _gainBits;
    private long _muteChanges;

    public MicrophoneState(bool mutedAtStart = false, double gain = 1.0)
    {
        _muted = mutedAtStart ? 1 : 0;
        Gain = gain;
    }

    public bool IsMuted => Volatile.Read(ref _muted) != 0;

    /// <summary>Times mute has been toggled, for diagnostics.</summary>
    public long MuteChanges => Interlocked.Read(ref _muteChanges);

    /// <summary>
    /// Linear gain, 0..2 where 1 is unity. Clamped on assignment so a bad settings
    /// value cannot produce a deafening track.
    /// </summary>
    public double Gain
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _gainBits));
        set => Interlocked.Exchange(
            ref _gainBits, BitConverter.DoubleToInt64Bits(Math.Clamp(value, 0.0, 2.0)));
    }

    /// <summary>Sets mute. Returns the new state.</summary>
    public bool SetMuted(bool muted)
    {
        var value = muted ? 1 : 0;

        if (Interlocked.Exchange(ref _muted, value) != value)
        {
            Interlocked.Increment(ref _muteChanges);
        }

        return muted;
    }

    /// <summary>Flips mute. Returns the new state.</summary>
    public bool Toggle()
    {
        // A loop rather than a plain exchange so two concurrent toggles cannot
        // both read "unmuted" and both write "muted".
        while (true)
        {
            var current = Volatile.Read(ref _muted);
            var next = current == 0 ? 1 : 0;

            if (Interlocked.CompareExchange(ref _muted, next, current) == current)
            {
                Interlocked.Increment(ref _muteChanges);
                return next != 0;
            }
        }
    }
}
