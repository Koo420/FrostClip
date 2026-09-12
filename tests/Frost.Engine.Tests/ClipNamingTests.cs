using Frost.Engine.Clips;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class ClipNamingTests
{
    private static readonly DateTimeOffset When =
        new(2026, 9, 12, 20, 11, 3, TimeSpan.Zero);

    [Fact]
    public void NameIsSortableAndCarriesGameAndPreset()
    {
        Assert.Equal(
            "Half-Life 2 2026-09-12 20-11-03 (30s).mp4",
            ClipNaming.BuildFileName(When, "Half-Life 2", "30s"));
    }

    [Fact]
    public void FallsBackToTheProductNameWithoutAGame()
    {
        Assert.Equal("Frost 2026-09-12 20-11-03.mp4", ClipNaming.BuildFileName(When));
    }

    [Theory]
    [InlineData("Rocket League: Season 12", "Rocket League Season 12")]
    [InlineData("Crash/Burn", "CrashBurn")]
    [InlineData("What?!*", "What!")]
    [InlineData("Half-Life 2.", "Half-Life 2")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("C:\\Games\\thing", "CGamesthing")]
    public void CharactersAPathCannotHoldAreStripped(string input, string expected) =>
        Assert.Equal(expected, ClipNaming.Sanitise(input));

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("LPT9", "LPT9_")]
    public void ReservedDeviceNamesAreNeutralised(string input, string expected)
    {
        // "CON.mp4" cannot be created on Windows, and the failure is baffling.
        Assert.Equal(expected, ClipNaming.Sanitise(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("...")]
    public void NothingUsableSanitisesToEmpty(string? input) =>
        Assert.Equal(string.Empty, ClipNaming.Sanitise(input));

    [Fact]
    public void ControlCharactersAreStripped() =>
        Assert.Equal("abc", ClipNaming.Sanitise("a\u0001b\tc"));

    [Fact]
    public void AVeryLongGameNameIsTruncatedToSomethingAPathCanHold()
    {
        var name = ClipNaming.BuildFileName(When, new string('x', 400), "15s");

        Assert.True(name.Length <= ClipNaming.MaxNameLength + 4);
        Assert.EndsWith(".mp4", name);
    }

    [Fact]
    public void FirstNameWinsWhenNothingExists()
    {
        var path = ClipNaming.MakeUniquePath("/clips", "a.mp4", _ => false);
        Assert.Equal(Path.Combine("/clips", "a.mp4"), path);
    }

    [Fact]
    public void CollisionsGetANumberedSuffixRatherThanOverwriting()
    {
        // Two hotkeys in the same second must not clobber each other's clip.
        var taken = new HashSet<string>
        {
            Path.Combine("/clips", "a.mp4"),
            Path.Combine("/clips", "a (2).mp4"),
        };

        Assert.Equal(
            Path.Combine("/clips", "a (3).mp4"),
            ClipNaming.MakeUniquePath("/clips", "a.mp4", taken.Contains));
    }

    [Fact]
    public void AbsurdNumbersOfCollisionsStillProduceAUniquePath()
    {
        var path = ClipNaming.MakeUniquePath("/clips", "a.mp4", _ => true);

        Assert.StartsWith(Path.Combine("/clips", "a ("), path);
        Assert.EndsWith(".mp4", path);
    }

    [Fact]
    public void EmptyArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ClipNaming.MakeUniquePath("", "a.mp4", _ => false));
        Assert.Throws<ArgumentException>(() => ClipNaming.MakeUniquePath("/clips", "", _ => false));
        Assert.Throws<ArgumentNullException>(() => ClipNaming.MakeUniquePath("/clips", "a.mp4", null!));
    }
}
