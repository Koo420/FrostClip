namespace Frost.Engine.Encoding;

/// <summary>Video codecs Frost can ask a hardware encoder for.</summary>
public enum VideoCodec
{
    /// <summary>
    /// The default. Every hardware encoder of the last decade does it and every
    /// player, editor and upload target accepts it without a re-encode.
    /// </summary>
    H264 = 0,

    /// <summary>Better quality per bit; needs a newer GPU and is fussier downstream.</summary>
    Hevc = 1,

    /// <summary>Best quality per bit; hardware support starts at Arc / RTX 40 / RDNA 3.</summary>
    Av1 = 2,
}

/// <summary>GPU vendor behind a hardware encoder.</summary>
public enum EncoderVendor
{
    Unknown = 0,

    /// <summary>NVENC.</summary>
    Nvidia = 1,

    /// <summary>AMF / VCE / VCN.</summary>
    Amd = 2,

    /// <summary>QuickSync.</summary>
    Intel = 3,

    /// <summary>Snapdragon-class ARM parts.</summary>
    Qualcomm = 4,

    /// <summary>Microsoft's own MFTs — software, and therefore never used.</summary>
    Microsoft = 5,
}

/// <summary>PCI vendor IDs, for matching an encoder to the adapter Frost captures on.</summary>
public static class PciVendors
{
    public const uint Nvidia = 0x10DE;
    public const uint Amd = 0x1002;
    public const uint AmdAlternate = 0x1022;
    public const uint Intel = 0x8086;
    public const uint Qualcomm = 0x5143;
    public const uint Microsoft = 0x1414;

    public static EncoderVendor ToVendor(uint vendorId) => vendorId switch
    {
        Nvidia => EncoderVendor.Nvidia,
        Amd or AmdAlternate => EncoderVendor.Amd,
        Intel => EncoderVendor.Intel,
        Qualcomm => EncoderVendor.Qualcomm,
        Microsoft => EncoderVendor.Microsoft,
        _ => EncoderVendor.Unknown,
    };

    /// <summary>
    /// Parses the <c>VEN_10DE</c> form Media Foundation reports in
    /// <c>MFT_ENUM_HARDWARE_VENDOR_ID_Attribute</c>.
    /// </summary>
    public static uint? ParseVendorId(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase))
        {
            span = span[4..];
        }

        return uint.TryParse(span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
