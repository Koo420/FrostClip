namespace Frost.Engine.Encoding;

/// <summary>
/// Builds Media Foundation media-subtype GUIDs from FOURCCs.
/// </summary>
/// <remarks>
/// Media Foundation derives an uncompressed or codec subtype GUID from a FOURCC
/// with the fixed template
/// <c>XXXXXXXX-0000-0010-8000-00AA00389B71</c>, where the first field is the
/// FOURCC in little-endian order. Computing it beats pasting in literals for the
/// subtypes Vortice's table does not carry (AV1, for one): a mistyped literal is
/// a runtime failure nothing catches.
/// </remarks>
public static class FourCc
{
    /// <summary>Subtype GUID for a four-character code.</summary>
    public static Guid ToMediaSubtype(char a, char b, char c, char d) =>
        new(
            (uint)(a | (b << 8) | (c << 16) | (d << 24)),
            0x0000, 0x0010,
            0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>Subtype GUID for a four-character code given as a string.</summary>
    public static Guid ToMediaSubtype(string fourCc)
    {
        ArgumentNullException.ThrowIfNull(fourCc);

        if (fourCc.Length != 4)
        {
            throw new ArgumentException($"'{fourCc}' is not four characters.", nameof(fourCc));
        }

        return ToMediaSubtype(fourCc[0], fourCc[1], fourCc[2], fourCc[3]);
    }

    /// <summary><c>MFVideoFormat_AV1</c>, which Vortice's subtype table omits.</summary>
    public static Guid Av1Subtype { get; } = ToMediaSubtype("AV01");
}
