using Frost.Engine.Encoding;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class EncoderSelectorTests
{
    private static EncoderDescriptor Hw(
        string name, VideoCodec codec, uint? vendorId = null, int index = 0) =>
        new(name, codec, IsHardware: true, vendorId, HardwareUrl: "mft://fake", EnumerationIndex: index);

    private static EncoderDescriptor Sw(string name, VideoCodec codec, int index = 0) =>
        new(name, codec, IsHardware: false, EnumerationIndex: index);

    [Fact]
    public void PicksTheHardwareEncoderForTheRequestedCodec()
    {
        var discovered = new[]
        {
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia),
            Hw("NVIDIA HEVC Encoder MFT", VideoCodec.Hevc, PciVendors.Nvidia),
        };

        var chosen = EncoderSelector.Select(discovered, new EncoderPreferences { Codec = VideoCodec.Hevc });

        Assert.Equal(VideoCodec.Hevc, chosen.Codec);
        Assert.Equal(EncoderVendor.Nvidia, chosen.Vendor);
    }

    [Fact]
    public void NeverSelectsASoftwareEncoderEvenWhenItIsTheOnlyOption()
    {
        // The non-negotiable one: a software fallback would spend the CPU
        // headroom the game needs, so this has to be an error.
        var discovered = new[]
        {
            Sw("Microsoft H264 Encoder MFT", VideoCodec.H264),
            Sw("Microsoft H265 Encoder MFT", VideoCodec.Hevc),
        };

        var ex = Assert.Throws<NoHardwareEncoderException>(
            () => EncoderSelector.Select(discovered, new EncoderPreferences()));

        Assert.Contains("No hardware video encoder", ex.Message);
        Assert.Contains("Microsoft H264 Encoder MFT", ex.Message);
        Assert.Contains("will not use them", ex.Message);
    }

    [Fact]
    public void ErrorNamesTheCodecsThatAreAvailableInHardware()
    {
        var discovered = new[]
        {
            Hw("Intel H.264 Encoder MFT", VideoCodec.H264, PciVendors.Intel),
            Hw("Intel HEVC Encoder MFT", VideoCodec.Hevc, PciVendors.Intel),
        };

        var ex = Assert.Throws<NoHardwareEncoderException>(() => EncoderSelector.Select(
            discovered,
            new EncoderPreferences { Codec = VideoCodec.Av1, AllowCodecFallback = false }));

        Assert.Contains("AV1", ex.Message);
        Assert.Contains("H.264", ex.Message);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public void ErrorTellsTheUserWhatToCheckWhenThereIsNoHardwareEncoderAtAll()
    {
        var ex = Assert.Throws<NoHardwareEncoderException>(
            () => EncoderSelector.Select([], new EncoderPreferences()));

        Assert.Contains("NVENC", ex.Message);
        Assert.Contains("QuickSync", ex.Message);
        Assert.Contains("driver", ex.Message);
    }

    [Fact]
    public void FallsBackToAnotherCodecWhenAllowed()
    {
        var discovered = new[] { Hw("AMD H.264 Encoder", VideoCodec.H264, PciVendors.Amd) };

        var chosen = EncoderSelector.Select(
            discovered, new EncoderPreferences { Codec = VideoCodec.Av1, AllowCodecFallback = true });

        Assert.Equal(VideoCodec.H264, chosen.Codec);
    }

    [Fact]
    public void FallbackPrefersH264ThenHevcThenAv1()
    {
        var discovered = new[]
        {
            Hw("Vendor AV1", VideoCodec.Av1, PciVendors.Intel, index: 0),
            Hw("Vendor HEVC", VideoCodec.Hevc, PciVendors.Intel, index: 1),
            Hw("Vendor H264", VideoCodec.H264, PciVendors.Intel, index: 2),
        };

        // Requesting a codec that exists gets that codec; the fallback order only
        // applies when it does not.
        Assert.Equal(
            VideoCodec.Av1,
            EncoderSelector.Select(discovered, new EncoderPreferences { Codec = VideoCodec.Av1 }).Codec);

        var withoutHevc = discovered.Where(d => d.Codec != VideoCodec.Hevc).ToList();
        Assert.Equal(
            VideoCodec.H264,
            EncoderSelector.Select(withoutHevc, new EncoderPreferences { Codec = VideoCodec.Hevc }).Codec);
    }

    [Fact]
    public void DoesNotFallBackWhenTheUserAskedForAnExactCodec()
    {
        var discovered = new[] { Hw("AMD H.264 Encoder", VideoCodec.H264, PciVendors.Amd) };

        Assert.Throws<NoHardwareEncoderException>(() => EncoderSelector.Select(
            discovered, new EncoderPreferences { Codec = VideoCodec.Av1, AllowCodecFallback = false }));
    }

    [Fact]
    public void PrefersTheEncoderOnTheAdapterCaptureIsRunningOn()
    {
        // Hybrid laptop: encoding on the other GPU means a cross-adapter copy of
        // every single frame.
        var discovered = new[]
        {
            Hw("Intel H.264 Encoder MFT", VideoCodec.H264, PciVendors.Intel, index: 0),
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia, index: 1),
        };

        var chosen = EncoderSelector.Select(
            discovered,
            new EncoderPreferences { CaptureAdapterVendorId = PciVendors.Nvidia });

        Assert.Equal(EncoderVendor.Nvidia, chosen.Vendor);
    }

    [Fact]
    public void AdapterAffinityWorksFromTheNameWhenTheDriverOmitsTheVendorId()
    {
        var discovered = new[]
        {
            Hw("Intel Quick Sync Video H.264 Encoder MFT", VideoCodec.H264, vendorId: null, index: 0),
            Hw("AMD AMF H.264 Encoder", VideoCodec.H264, vendorId: null, index: 1),
        };

        var chosen = EncoderSelector.Select(
            discovered, new EncoderPreferences { CaptureAdapterVendorId = PciVendors.Amd });

        Assert.Equal(EncoderVendor.Amd, chosen.Vendor);
    }

    [Fact]
    public void FallsBackToMediaFoundationsOwnOrderWhenNothingElseDistinguishes()
    {
        var discovered = new[]
        {
            Hw("Second", VideoCodec.H264, index: 5),
            Hw("First", VideoCodec.H264, index: 1),
        };

        Assert.Equal("First", EncoderSelector.Select(discovered, new EncoderPreferences()).Name);
    }

    [Fact]
    public void AnExplicitEncoderNameWinsOverAdapterAffinity()
    {
        var discovered = new[]
        {
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia),
            Hw("Intel H.264 Encoder MFT", VideoCodec.H264, PciVendors.Intel),
        };

        var chosen = EncoderSelector.Select(discovered, new EncoderPreferences
        {
            PreferredEncoderName = "intel h.264 encoder mft",
            CaptureAdapterVendorId = PciVendors.Nvidia,
        });

        Assert.Equal(EncoderVendor.Intel, chosen.Vendor);
    }

    [Fact]
    public void AnExplicitNameThatNoLongerExistsFallsBackInsteadOfFailing()
    {
        // The user's chosen GPU was removed, or its driver was uninstalled.
        // Recording with the remaining hardware encoder beats not recording.
        var discovered = new[] { Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia) };

        var chosen = EncoderSelector.Select(
            discovered, new EncoderPreferences { PreferredEncoderName = "A GPU that was unplugged" });

        Assert.Equal("NVIDIA H.264 Encoder MFT", chosen.Name);
    }

    [Fact]
    public void AnExplicitNamePointingAtASoftwareEncoderIsIgnored()
    {
        var discovered = new[]
        {
            Sw("Microsoft H264 Encoder MFT", VideoCodec.H264),
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia),
        };

        var chosen = EncoderSelector.Select(
            discovered, new EncoderPreferences { PreferredEncoderName = "Microsoft H264 Encoder MFT" });

        Assert.True(chosen.IsHardware);
        Assert.Equal(EncoderVendor.Nvidia, chosen.Vendor);
    }

    [Fact]
    public void TrySelectReportsFailureWithoutThrowing()
    {
        Assert.False(EncoderSelector.TrySelect([], new EncoderPreferences(), out var chosen));
        Assert.Null(chosen);

        var discovered = new[] { Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia) };
        Assert.True(EncoderSelector.TrySelect(discovered, new EncoderPreferences(), out chosen));
        Assert.NotNull(chosen);
    }

    [Fact]
    public void AvailableCodecsListsOnlyWhatHardwareCanDo()
    {
        var discovered = new[]
        {
            Hw("NVIDIA HEVC Encoder MFT", VideoCodec.Hevc, PciVendors.Nvidia),
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia),
            Sw("Microsoft AV1 Encoder MFT", VideoCodec.Av1),
        };

        Assert.Equal(
            new[] { VideoCodec.H264, VideoCodec.Hevc },
            EncoderSelector.AvailableCodecs(discovered));
    }

    [Theory]
    [InlineData("VEN_10DE", EncoderVendor.Nvidia)]
    [InlineData("ven_10de", EncoderVendor.Nvidia)]
    [InlineData("10DE", EncoderVendor.Nvidia)]
    [InlineData("VEN_1002", EncoderVendor.Amd)]
    [InlineData("VEN_1022", EncoderVendor.Amd)]
    [InlineData("VEN_8086", EncoderVendor.Intel)]
    [InlineData("VEN_5143", EncoderVendor.Qualcomm)]
    [InlineData("VEN_1414", EncoderVendor.Microsoft)]
    public void VendorIdsMapToVendors(string raw, EncoderVendor expected)
    {
        var parsed = PciVendors.ParseVendorId(raw);
        Assert.NotNull(parsed);
        Assert.Equal(expected, PciVendors.ToVendor(parsed!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VEN_")]
    [InlineData("not a vendor")]
    public void UnparseableVendorIdsAreNull(string? raw) =>
        Assert.Null(PciVendors.ParseVendorId(raw));

    [Fact]
    public void UnknownVendorIdDoesNotClaimAffinity()
    {
        var discovered = new[]
        {
            Hw("Mystery H.264 Encoder", VideoCodec.H264, vendorId: 0xBEEF, index: 3),
            Hw("NVIDIA H.264 Encoder MFT", VideoCodec.H264, PciVendors.Nvidia, index: 4),
        };

        var chosen = EncoderSelector.Select(
            discovered, new EncoderPreferences { CaptureAdapterVendorId = PciVendors.Nvidia });

        Assert.Equal(EncoderVendor.Nvidia, chosen.Vendor);
    }
}

public sealed class FourCcTests
{
    [Fact]
    public void Av1SubtypeMatchesMFVideoFormatAv1()
    {
        // MFVideoFormat_AV1 as documented by Windows: the "AV01" FOURCC in
        // little-endian order followed by the standard subtype template.
        Assert.Equal(new Guid("31305641-0000-0010-8000-00AA00389B71"), FourCc.Av1Subtype);
    }

    [Theory]
    // Known Media Foundation subtypes, used as independent checks on the template.
    [InlineData("NV12", "3231564E-0000-0010-8000-00AA00389B71")]
    [InlineData("H264", "34363248-0000-0010-8000-00AA00389B71")]
    [InlineData("HEVC", "43564548-0000-0010-8000-00AA00389B71")]
    [InlineData("P010", "30313050-0000-0010-8000-00AA00389B71")]
    public void FourCcsMapToTheirKnownSubtypeGuids(string fourCc, string expected) =>
        Assert.Equal(new Guid(expected), FourCc.ToMediaSubtype(fourCc));

    [Theory]
    [InlineData("NV1")]
    [InlineData("NV123")]
    [InlineData("")]
    public void OnlyFourCharacterCodesAreAccepted(string bad) =>
        Assert.Throws<ArgumentException>(() => FourCc.ToMediaSubtype(bad));
}
