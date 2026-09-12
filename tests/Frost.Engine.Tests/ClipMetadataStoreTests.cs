using Frost.Shared.Clips;
using Xunit;

namespace Frost.Engine.Tests;

public sealed class ClipMetadataStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "frost-metadata-tests", Guid.NewGuid().ToString("N"));

    public ClipMetadataStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private ClipMetadata Sample(string name = "clip.mp4") => new()
    {
        FilePath = Path.Combine(_directory, name),
        DisplayName = "A great play",
        CreatedUtc = new DateTimeOffset(2026, 9, 12, 20, 11, 3, TimeSpan.Zero),
        DurationTicks = TimeSpan.FromSeconds(30).Ticks,
        SizeBytes = 46_500_000,
        Width = 1920,
        Height = 1080,
        Codec = "H264",
        Kind = ClipKind.InstantClip,
        GameName = "Half-Life 2",
        Bookmarks =
        [
            new Bookmark(TimeSpan.FromSeconds(4).Ticks, BookmarkSource.Manual, "the shot"),
            new Bookmark(TimeSpan.FromSeconds(11).Ticks, BookmarkSource.AudioLoudnessSpike),
        ],
    };

    [Fact]
    public void RoundTripsThroughTheSidecar()
    {
        var metadata = Sample();
        File.WriteAllText(metadata.FilePath, "not really a video");

        ClipMetadataStore.Save(metadata);
        var loaded = ClipMetadataStore.TryLoad(metadata.FilePath);

        Assert.NotNull(loaded);
        Assert.Equal("A great play", loaded!.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(30), loaded.Duration);
        Assert.Equal("Half-Life 2", loaded.GameName);
        Assert.Equal(2, loaded.Bookmarks.Count);
        Assert.Equal("the shot", loaded.Bookmarks[0].Label);
        Assert.Equal(BookmarkSource.AudioLoudnessSpike, loaded.Bookmarks[1].Source);
        Assert.Equal(TimeSpan.FromSeconds(11), loaded.Bookmarks[1].Offset);
    }

    [Fact]
    public void SidecarSitsNextToTheClip()
    {
        var metadata = Sample();
        ClipMetadataStore.Save(metadata);

        Assert.True(File.Exists(metadata.FilePath + ".frost.json"));
        Assert.Equal(metadata.FilePath + ".frost.json", ClipMetadata.SidecarPathFor(metadata.FilePath));
    }

    [Fact]
    public void SavingLeavesNoTemporaryFileBehind()
    {
        // The save writes to a temp file and moves it, so an interrupted write
        // cannot leave a half-written sidecar. The temp must not survive.
        var metadata = Sample();
        ClipMetadataStore.Save(metadata);

        Assert.False(File.Exists(metadata.FilePath + ".frost.json.tmp"));
    }

    [Fact]
    public void SavingTwiceOverwritesCleanly()
    {
        var metadata = Sample();
        ClipMetadataStore.Save(metadata);
        ClipMetadataStore.Save(metadata with { DisplayName = "Renamed" });

        Assert.Equal("Renamed", ClipMetadataStore.TryLoad(metadata.FilePath)!.DisplayName);
    }

    [Fact]
    public void AMissingSidecarLoadsAsNullRatherThanThrowing() =>
        Assert.Null(ClipMetadataStore.TryLoad(Path.Combine(_directory, "absent.mp4")));

    [Fact]
    public void ACorruptSidecarDoesNotHideTheClip()
    {
        // A clip whose sidecar got truncated must still appear in the gallery.
        var clipPath = Path.Combine(_directory, "broken.mp4");
        File.WriteAllText(clipPath, "video");
        File.WriteAllText(ClipMetadata.SidecarPathFor(clipPath), "{ this is not json");

        Assert.Null(ClipMetadataStore.TryLoad(clipPath));

        var described = ClipMetadataStore.Describe(clipPath);
        Assert.Equal(clipPath, described.FilePath);
        Assert.Equal("broken", described.DisplayName);
        Assert.Equal(5, described.SizeBytes);
    }

    [Fact]
    public void DescribePrefersTheSidecarWhenItIsValid()
    {
        var metadata = Sample("described.mp4");
        File.WriteAllText(metadata.FilePath, "video");
        ClipMetadataStore.Save(metadata);

        Assert.Equal("A great play", ClipMetadataStore.Describe(metadata.FilePath).DisplayName);
    }

    [Fact]
    public void DescribeWorksForAFileThatDoesNotExist()
    {
        var described = ClipMetadataStore.Describe(Path.Combine(_directory, "gone.mp4"));
        Assert.Equal(0, described.SizeBytes);
        Assert.Equal("gone", described.DisplayName);
    }

    [Fact]
    public void DeleteRemovesBothTheClipAndTheSidecar()
    {
        var metadata = Sample("doomed.mp4");
        File.WriteAllText(metadata.FilePath, "video");
        ClipMetadataStore.Save(metadata);

        ClipMetadataStore.Delete(metadata.FilePath);

        Assert.False(File.Exists(metadata.FilePath));
        Assert.False(File.Exists(ClipMetadata.SidecarPathFor(metadata.FilePath)));
    }

    [Fact]
    public void DeletingWhatIsAlreadyGoneIsNotAnError() =>
        ClipMetadataStore.Delete(Path.Combine(_directory, "never-existed.mp4"));

    [Fact]
    public void EmptyPathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ClipMetadataStore.TryLoad(""));
        Assert.Throws<ArgumentException>(() => ClipMetadataStore.Describe(" "));
        Assert.Throws<ArgumentException>(() => ClipMetadataStore.Delete(""));
        Assert.Throws<ArgumentNullException>(() => ClipMetadataStore.Save(null!));
    }
}
