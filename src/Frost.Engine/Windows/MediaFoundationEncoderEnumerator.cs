using System.Runtime.InteropServices;
using Frost.Engine.Diagnostics;
using Frost.Engine.Encoding;
using Vortice.MediaFoundation;

namespace Frost.Engine.Windows;

/// <summary>
/// Asks Media Foundation what hardware video encoders this machine has.
/// </summary>
/// <remarks>
/// Runs once at startup, before any capture, so the "no hardware encoder" case
/// is reported up front rather than discovered on the first clip.
/// <para>
/// <c>MFTEnumEx</c> is queried with <c>MFT_ENUM_FLAG_HARDWARE</c> per codec.
/// Hardware and software passes are kept separate so the error message can say
/// what software encoders were deliberately ignored — the difference between a
/// user understanding the problem and filing a bug.
/// </para>
/// </remarks>
internal sealed class MediaFoundationEncoderEnumerator
{
    private readonly IEngineLog _log;

    internal MediaFoundationEncoderEnumerator(IEngineLog log) => _log = log;

    /// <summary>
    /// Everything the system reports, hardware first. Software entries are
    /// included so callers can explain what was rejected; they are never
    /// selectable (<see cref="EncoderSelector"/> filters them out).
    /// </summary>
    internal List<EncoderDescriptor> Enumerate()
    {
        var results = new List<EncoderDescriptor>();

        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
        {
            Collect(codec, hardware: true, results);
        }

        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
        {
            Collect(codec, hardware: false, results);
        }

        if (results.Count == 0)
        {
            _log.Warn("Media Foundation reported no video encoders at all.");
        }
        else
        {
            foreach (var descriptor in results)
            {
                _log.Info($"Encoder: {descriptor}");
            }
        }

        return results;
    }

    private void Collect(VideoCodec codec, bool hardware, List<EncoderDescriptor> results)
    {
        // Input NV12 / output the codec's subtype: the shape every hardware
        // encoder MFT advertises, and the shape Frost actually feeds it.
        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12,
        };

        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = SubtypeFor(codec),
        };

        var flags = (uint)(EnumFlag.EnumFlagSortandfilter |
                           (hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft));

        nint activateArray;
        uint count;

        try
        {
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder, flags, input, output, out activateArray, out count);
        }
        catch (Exception ex)
        {
            // A driver with a broken MFT registration should not stop the other
            // codecs from being discovered.
            _log.Warn($"MFTEnumEx failed for {codec} ({(hardware ? "hardware" : "software")}).", ex);
            return;
        }

        if (activateArray == 0 || count == 0)
        {
            return;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var activatePointer = Marshal.ReadIntPtr(activateArray, i * nint.Size);
                if (activatePointer == 0)
                {
                    continue;
                }

                using var activate = new IMFActivate(activatePointer);
                var descriptor = Describe(activate, codec, hardware, results.Count);

                // Two codecs can be served by one MFT; do not list it twice.
                if (!results.Any(existing =>
                        existing.Codec == descriptor.Codec &&
                        existing.IsHardware == descriptor.IsHardware &&
                        string.Equals(existing.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(descriptor);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activateArray);
        }
    }

    private EncoderDescriptor Describe(IMFActivate activate, VideoCodec codec, bool hardware, int index)
    {
        var name = TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute)
                   ?? $"Unnamed {codec} encoder";

        var vendorId = PciVendors.ParseVendorId(
            TryGetString(activate, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute));

        var url = TryGetString(activate, TransformAttributeKeys.MftEnumHardwareUrlAttribute);

        // A hardware URL is Media Foundation's own marker for a hardware MFT, so
        // trust it over the enumeration flag we asked with.
        var isHardware = hardware || url is { Length: > 0 };

        return new EncoderDescriptor(name, codec, isHardware, vendorId, url, index);
    }

    private string? TryGetString(IMFActivate activate, Guid key)
    {
        try
        {
            var value = activate.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            // Absent attributes are normal, not an error.
            _log.Debug($"Encoder attribute {key} not present.");
            return null;
        }
    }

    private static Guid SubtypeFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => VideoFormatGuids.H264,
        VideoCodec.Hevc => VideoFormatGuids.Hevc,
        VideoCodec.Av1 => FourCc.Av1Subtype,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unknown codec."),
    };
}
