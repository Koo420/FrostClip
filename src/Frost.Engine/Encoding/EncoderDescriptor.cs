namespace Frost.Engine.Encoding;

/// <summary>
/// One encoder the system offers, as reported at startup.
/// </summary>
/// <param name="Name">Friendly name, e.g. "NVIDIA H.264 Encoder MFT".</param>
/// <param name="Codec">What it encodes to.</param>
/// <param name="IsHardware">
/// Whether Media Foundation registered it as a hardware MFT. Frost only ever
/// uses encoders where this is true.
/// </param>
/// <param name="VendorId">PCI vendor ID, when the MFT reports one.</param>
/// <param name="HardwareUrl">The MFT's hardware URL, when it reports one.</param>
/// <param name="EnumerationIndex">Position in Media Foundation's own preference order.</param>
public sealed record EncoderDescriptor(
    string Name,
    VideoCodec Codec,
    bool IsHardware,
    uint? VendorId = null,
    string? HardwareUrl = null,
    int EnumerationIndex = 0)
{
    public EncoderVendor Vendor => VendorId is { } id
        ? PciVendors.ToVendor(id)
        : GuessVendorFromName(Name);

    /// <summary>
    /// Falls back to the friendly name when the MFT does not report a vendor ID.
    /// Some drivers omit it; the names are stable enough to be useful for display
    /// and for adapter affinity.
    /// </summary>
    private static EncoderVendor GuessVendorFromName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("NVENC", StringComparison.OrdinalIgnoreCase))
        {
            return EncoderVendor.Nvidia;
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AMF", StringComparison.OrdinalIgnoreCase))
        {
            return EncoderVendor.Amd;
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("QuickSync", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Quick Sync", StringComparison.OrdinalIgnoreCase))
        {
            return EncoderVendor.Intel;
        }

        if (name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase))
        {
            return EncoderVendor.Qualcomm;
        }

        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return EncoderVendor.Microsoft;
        }

        return EncoderVendor.Unknown;
    }

    public override string ToString() =>
        $"{Name} [{Codec}, {Vendor}, {(IsHardware ? "hardware" : "software")}" +
        $"{(VendorId is { } id ? $", VEN_{id:X4}" : string.Empty)}]";
}
