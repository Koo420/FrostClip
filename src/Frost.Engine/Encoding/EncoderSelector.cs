namespace Frost.Engine.Encoding;

/// <summary>What the user asked for, as far as encoder choice goes.</summary>
public sealed record EncoderPreferences
{
    /// <summary>Codec to try first. H.264 is the compatible default.</summary>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>
    /// Exact encoder name to use, for the machine with two GPUs where the user
    /// knows which one they want. Takes precedence over everything else.
    /// </summary>
    public string? PreferredEncoderName { get; init; }

    /// <summary>
    /// PCI vendor ID of the adapter capture is running on. An encoder on the same
    /// GPU avoids a cross-adapter copy of every frame, which on a hybrid laptop
    /// is the difference between free and very much not free.
    /// </summary>
    public uint? CaptureAdapterVendorId { get; init; }

    /// <summary>
    /// Fall back to another codec when the requested one has no hardware encoder.
    /// A user who explicitly picked AV1 usually wants to know it is unavailable,
    /// but the default profile should still record.
    /// </summary>
    public bool AllowCodecFallback { get; init; } = true;
}

/// <summary>
/// Picks which hardware encoder to use. Pure function of the discovered
/// encoders and the user's preferences, so the policy is fully testable.
/// </summary>
/// <remarks>
/// The one rule that is not negotiable: software encoders are never selected, no
/// matter what is missing. If nothing suitable exists the caller gets a
/// <see cref="NoHardwareEncoderException"/> whose message says what to do about
/// it, rather than a quiet fallback that eats the game's CPU budget.
/// </remarks>
public static class EncoderSelector
{
    /// <summary>Codecs tried, in order, when the requested one is unavailable.</summary>
    private static readonly VideoCodec[] FallbackOrder =
        [VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1];

    public static EncoderDescriptor Select(
        IReadOnlyList<EncoderDescriptor> discovered,
        EncoderPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(preferences);

        var hardware = discovered.Where(d => d.IsHardware).ToList();

        if (preferences.PreferredEncoderName is { Length: > 0 } name)
        {
            var exact = hardware.FirstOrDefault(
                d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                return exact;
            }
        }

        if (TrySelectForCodec(hardware, preferences.Codec, preferences, out var chosen))
        {
            return chosen;
        }

        if (preferences.AllowCodecFallback)
        {
            foreach (var codec in FallbackOrder)
            {
                if (codec != preferences.Codec &&
                    TrySelectForCodec(hardware, codec, preferences, out var fallback))
                {
                    return fallback;
                }
            }
        }

        throw new NoHardwareEncoderException(preferences.Codec, discovered);
    }

    /// <summary>
    /// Selection without throwing, for the settings UI listing what is possible.
    /// </summary>
    public static bool TrySelect(
        IReadOnlyList<EncoderDescriptor> discovered,
        EncoderPreferences preferences,
        out EncoderDescriptor? chosen)
    {
        try
        {
            chosen = Select(discovered, preferences);
            return true;
        }
        catch (NoHardwareEncoderException)
        {
            chosen = null;
            return false;
        }
    }

    /// <summary>Codecs this machine can encode in hardware, in Frost's preference order.</summary>
    public static IReadOnlyList<VideoCodec> AvailableCodecs(IReadOnlyList<EncoderDescriptor> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        return FallbackOrder
            .Where(codec => discovered.Any(d => d.IsHardware && d.Codec == codec))
            .ToList();
    }

    private static bool TrySelectForCodec(
        List<EncoderDescriptor> hardware,
        VideoCodec codec,
        EncoderPreferences preferences,
        out EncoderDescriptor chosen)
    {
        var candidates = hardware.Where(d => d.Codec == codec).ToList();

        if (candidates.Count == 0)
        {
            chosen = null!;
            return false;
        }

        // Prefer the encoder on the GPU we are capturing on; then Media
        // Foundation's own order, which already reflects the driver's preference.
        chosen = candidates
            .OrderByDescending(d => MatchesCaptureAdapter(d, preferences.CaptureAdapterVendorId))
            .ThenBy(d => d.EnumerationIndex)
            .First();

        return true;
    }

    private static bool MatchesCaptureAdapter(EncoderDescriptor descriptor, uint? adapterVendorId)
    {
        if (adapterVendorId is not { } vendorId)
        {
            return false;
        }

        if (descriptor.VendorId == vendorId)
        {
            return true;
        }

        // Fall back to vendor identity so a driver that omits the vendor ID
        // attribute still gets adapter affinity.
        return descriptor.Vendor != EncoderVendor.Unknown &&
               descriptor.Vendor == PciVendors.ToVendor(vendorId);
    }
}
