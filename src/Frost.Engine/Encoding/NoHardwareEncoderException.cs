namespace Frost.Engine.Encoding;

/// <summary>
/// Thrown when no usable hardware video encoder exists.
/// </summary>
/// <remarks>
/// Deliberately fatal. Frost will not fall back to a software encode: that would
/// spend the CPU headroom the game needs, which is the one thing a clipping app
/// must never do. The message names what was found and what was rejected, so the
/// user gets something actionable instead of "encoding failed".
/// </remarks>
public sealed class NoHardwareEncoderException : Exception
{
    public NoHardwareEncoderException(
        VideoCodec requestedCodec,
        IReadOnlyList<EncoderDescriptor> discovered)
        : base(BuildMessage(requestedCodec, discovered))
    {
        RequestedCodec = requestedCodec;
        Discovered = discovered;
    }

    public VideoCodec RequestedCodec { get; }

    /// <summary>Everything enumeration turned up, hardware or not.</summary>
    public IReadOnlyList<EncoderDescriptor> Discovered { get; }

    private static string BuildMessage(VideoCodec requestedCodec, IReadOnlyList<EncoderDescriptor> discovered)
    {
        var software = discovered.Where(d => !d.IsHardware).ToList();
        var otherHardware = discovered.Where(d => d.IsHardware && d.Codec != requestedCodec).ToList();

        var message = new System.Text.StringBuilder();
        message.Append("No hardware video encoder is available for ");
        message.Append(Describe(requestedCodec));
        message.Append('.');

        if (otherHardware.Count > 0)
        {
            message.Append(" This GPU does have hardware encoders for ");
            message.Append(string.Join(", ", otherHardware.Select(d => Describe(d.Codec)).Distinct()));
            message.Append(" — choose one of those in Settings.");
        }
        else
        {
            message.Append(
                " Frost needs a GPU with a hardware video encoder (NVIDIA NVENC, " +
                "AMD AMF or Intel QuickSync). Check that the GPU's driver is installed " +
                "and that the display is connected to that GPU rather than the motherboard.");
        }

        if (software.Count > 0)
        {
            message.Append(" (Software encoders were found and ignored: ");
            message.Append(string.Join(", ", software.Select(d => d.Name)));
            message.Append(". Frost will not use them, because a software encode would " +
                           "take CPU away from the game.)");
        }

        return message.ToString();
    }

    private static string Describe(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => codec.ToString(),
    };
}
