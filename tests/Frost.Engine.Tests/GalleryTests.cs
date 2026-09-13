using Frost.Shared.Clips;
using Frost.Shared.Shell;
using Xunit;

namespace Frost.Engine.Tests;

/// <summary>
/// The gallery's policy: ordering, filtering, rename validation and the
/// keyframe-aligned trim that makes "quick trim" a re-mux rather than a
/// re-encode.
/// </summary>
public sealed class GalleryTests
{
    private static ClipMetadata Clip(
        string name,
        string? game = null,
        int daysAgo = 0,
        long sizeBytes = 50 * 1024 * 1024,
        double seconds = 30,
        ClipKind kind = ClipKind.InstantClip,
        IReadOnlyList<Bookmark>? bookmarks = null,
        string directory = @"C:\Videos\Frost") => new()
    {
        // Joined with a literal backslash rather than Path.Combine: these are
        // Windows paths, and on the Linux build host Path.Combine would splice
        // them with a forward slash.
        FilePath = directory + @"\" + name + ".mp4",
        DisplayName = name,
        CreatedUtc = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero)
            .AddDays(-daysAgo),
        DurationTicks = (long)(seconds * TimeSpan.TicksPerSecond),
        SizeBytes = sizeBytes,
        Width = 1920,
        Height = 1080,
        Codec = "H264",
        Kind = kind,
        GameName = game,
        Bookmarks = bookmarks ?? [],
    };

    // ---- ordering and filtering -------------------------------------------

    [Fact]
    public void TheDefaultOrderIsNewestFirst()
    {
        var clips = new[]
        {
            Clip("old", daysAgo: 5),
            Clip("newest", daysAgo: 0),
            Clip("middle", daysAgo: 2),
        };

        Assert.Equal(
            ["newest", "middle", "old"],
            GalleryView.Apply(clips, new GalleryFilter()).Select(c => c.DisplayName));
    }

    [Fact]
    public void ClipsSavedInTheSameSecondKeepTheirOrderAcrossRefreshes()
    {
        // Two clips from one firefight share a timestamp. An unstable sort makes
        // their thumbnails swap places on every refresh, under the cursor.
        var clips = new[] { Clip("first"), Clip("second"), Clip("third") };

        var once = GalleryView.Apply(clips, new GalleryFilter()).Select(c => c.DisplayName);
        var twice = GalleryView.Apply(clips, new GalleryFilter()).Select(c => c.DisplayName);

        Assert.Equal(once, twice);
        Assert.Equal(["first", "second", "third"], once);
    }

    [Fact]
    public void LargestFirstIsTheOrderForFreeingSpace()
    {
        var clips = new[]
        {
            Clip("small", sizeBytes: 10),
            Clip("huge", sizeBytes: 9_000),
            Clip("medium", sizeBytes: 500),
        };

        Assert.Equal(
            ["huge", "medium", "small"],
            GalleryView
                .Apply(clips, new GalleryFilter(GallerySort.LargestFirst))
                .Select(c => c.DisplayName));
    }

    [Fact]
    public void SortingByNameIgnoresCase()
    {
        var clips = new[] { Clip("zulu"), Clip("Alpha"), Clip("mike") };

        Assert.Equal(
            ["Alpha", "mike", "zulu"],
            GalleryView
                .Apply(clips, new GalleryFilter(GallerySort.NameAscending))
                .Select(c => c.DisplayName));
    }

    [Fact]
    public void SearchMatchesTheNameOrTheGameCaseInsensitively()
    {
        var clips = new[]
        {
            Clip("clutch ace", game: "cs2"),
            Clip("whiff", game: "Valorant"),
            Clip("ACE in the hole", game: "Apex"),
        };

        Assert.Equal(
            ["clutch ace", "ACE in the hole"],
            GalleryView
                .Apply(clips, new GalleryFilter(Search: "ace"))
                .Select(c => c.DisplayName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Reverse());

        Assert.Equal(
            ["whiff"],
            GalleryView
                .Apply(clips, new GalleryFilter(Search: "valorant"))
                .Select(c => c.DisplayName));
    }

    [Fact]
    public void SearchTermsAreTrimmedSoATrailingSpaceStillMatches()
    {
        var clips = new[] { Clip("clutch") };

        Assert.Single(GalleryView.Apply(clips, new GalleryFilter(Search: " clutch ")));
    }

    [Fact]
    public void AClipWithNoGameIsNotMatchedByAGameSearch()
    {
        // A null GameName must not throw, and must not match everything.
        var clips = new[] { Clip("mystery", game: null) };

        Assert.Empty(GalleryView.Apply(clips, new GalleryFilter(Search: "cs2")));
        Assert.Single(GalleryView.Apply(clips, new GalleryFilter(Search: "mystery")));
    }

    [Fact]
    public void FiltersCombine()
    {
        var clips = new[]
        {
            Clip("a", game: "cs2", kind: ClipKind.InstantClip),
            Clip("b", game: "cs2", kind: ClipKind.FullSession),
            Clip("c", game: "Apex", kind: ClipKind.InstantClip),
        };

        var shown = GalleryView.Apply(
            clips,
            new GalleryFilter(Kind: ClipKind.InstantClip, Game: "CS2"));

        Assert.Equal(["a"], shown.Select(c => c.DisplayName));
    }

    [Fact]
    public void BookmarkedOnlyShowsOnlyMarkedRecordings()
    {
        var clips = new[]
        {
            Clip("plain"),
            Clip("marked", bookmarks: [new Bookmark(0, BookmarkSource.Manual)]),
        };

        Assert.Equal(
            ["marked"],
            GalleryView
                .Apply(clips, new GalleryFilter(BookmarkedOnly: true))
                .Select(c => c.DisplayName));
    }

    [Fact]
    public void TheGameListIsDistinctSortedAndSkipsUnknowns()
    {
        var clips = new[]
        {
            Clip("a", game: "cs2"),
            Clip("b", game: "CS2"),
            Clip("c", game: null),
            Clip("d", game: "  "),
            Clip("e", game: "Apex"),
        };

        Assert.Equal(["Apex", "cs2"], GalleryView.Games(clips));
    }

    [Fact]
    public void TotalBytesSaturatesRatherThanWrappingNegative()
    {
        // A corrupt sidecar reporting long.MaxValue must not turn the storage
        // readout into a negative number.
        var clips = new[] { Clip("a", sizeBytes: long.MaxValue), Clip("b", sizeBytes: 1024) };

        Assert.Equal(long.MaxValue, GalleryView.TotalBytes(clips));
        Assert.True(GalleryView.TotalBytes(clips) > 0);
    }

    [Fact]
    public void AnEmptyGridDistinguishesNoClipsFromNoMatches()
    {
        var none = Array.Empty<ClipMetadata>();
        var some = new[] { Clip("clutch") };

        Assert.Equal(
            GalleryEmptyReason.NoClipsAtAll,
            GalleryView.EmptyReason(none, none, new GalleryFilter()));

        // Four hundred clips and a typo in the search box must not be told
        // "no clips yet — press Alt+F10 while playing".
        Assert.Equal(
            GalleryEmptyReason.FilteredOut,
            GalleryView.EmptyReason(some, none, new GalleryFilter(Search: "shroud")));

        Assert.Equal(
            GalleryEmptyReason.NotEmpty,
            GalleryView.EmptyReason(some, some, new GalleryFilter()));
    }

    [Theory]
    // Deleting mid-list lands on the clip that moved up into the slot.
    [InlineData(1, 4, 1)]
    // Deleting the last one lands on the new last one, not off the end.
    [InlineData(3, 4, 2)]
    [InlineData(0, 2, 0)]
    // Deleting the only clip leaves nothing selected.
    [InlineData(0, 1, -1)]
    [InlineData(0, 0, -1)]
    [InlineData(5, 4, -1)]
    public void DeletingAClipKeepsARunOfDeletesGoing(
        int removedIndex,
        int countBefore,
        int expected) =>
        Assert.Equal(expected, GalleryView.SelectionAfterRemoving(removedIndex, countBefore));

    // ---- rename ------------------------------------------------------------

    private static bool NothingExists(string _) => false;

    [Fact]
    public void ARenamePlanMovesTheSidecarWithTheVideo()
    {
        // Forgetting the sidecar is how a clip loses its bookmarks and its game
        // name while appearing to rename fine.
        var check = ClipRename.Check(Clip("before"), "after", NothingExists);

        Assert.True(check.IsAllowed);
        var plan = check.Plan!.Value;

        Assert.Equal(@"C:\Videos\Frost\after.mp4", plan.NewClipPath);
        Assert.Equal(@"C:\Videos\Frost\before.mp4.frost.json", plan.SidecarPath);
        Assert.Equal(@"C:\Videos\Frost\after.mp4.frost.json", plan.NewSidecarPath);
        Assert.Equal("after", plan.DisplayName);
    }

    [Fact]
    public void TheOriginalExtensionIsKeptSoTheUserNeverTypesIt()
    {
        var clip = Clip("before") with { FilePath = @"C:\Videos\Frost\before.mkv" };

        Assert.Equal(
            @"C:\Videos\Frost\after.mkv",
            ClipRename.Check(clip, "after", NothingExists).Plan!.Value.NewClipPath);
    }

    [Theory]
    [InlineData(null, RenameRejection.Empty)]
    [InlineData("", RenameRejection.Empty)]
    [InlineData("   ", RenameRejection.Empty)]
    [InlineData("has/slash", RenameRejection.InvalidCharacters)]
    [InlineData(@"has\backslash", RenameRejection.InvalidCharacters)]
    [InlineData("has:colon", RenameRejection.InvalidCharacters)]
    [InlineData("has?question", RenameRejection.InvalidCharacters)]
    [InlineData("CON", RenameRejection.ReservedName)]
    [InlineData("con", RenameRejection.ReservedName)]
    [InlineData("NUL.mp4", RenameRejection.ReservedName)]
    [InlineData("COM1", RenameRejection.ReservedName)]
    [InlineData("trailing.", RenameRejection.TrailingDotOrSpace)]
    [InlineData("trailing ", RenameRejection.TrailingDotOrSpace)]
    public void NamesWindowsCannotStoreAreRefusedWithTheReasonNamed(
        string? proposed,
        RenameRejection expected)
    {
        var check = ClipRename.Check(Clip("before"), proposed, NothingExists);

        Assert.False(check.IsAllowed);
        Assert.Equal(expected, check.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
        Assert.Null(check.Plan);
    }

    [Fact]
    public void ATrailingDotIsRefusedRatherThanSilentlyStripped()
    {
        // Windows accepts "clutch." in the API call and then stores "clutch", so
        // a gallery that allows it shows a name the user did not type.
        Assert.Equal(
            RenameRejection.TrailingDotOrSpace,
            ClipRename.Check(Clip("before"), "clutch.", NothingExists).Rejection);
    }

    [Fact]
    public void AnOverlongNameIsRefusedBeforeItBecomesAnUnusablePath()
    {
        var proposed = new string('x', ClipRename.MaximumNameLength + 1);

        Assert.Equal(
            RenameRejection.TooLong,
            ClipRename.Check(Clip("before"), proposed, NothingExists).Rejection);

        Assert.True(
            ClipRename
                .Check(Clip("before"), new string('x', ClipRename.MaximumNameLength), NothingExists)
                .IsAllowed);
    }

    [Fact]
    public void ANameAlreadyTakenInTheSameFolderIsRefused()
    {
        var check = ClipRename.Check(
            Clip("before"),
            "taken",
            path => path == @"C:\Videos\Frost\taken.mp4");

        Assert.Equal(RenameRejection.NameTaken, check.Rejection);
        Assert.Contains("taken", check.Message);
    }

    [Fact]
    public void RenamingAClipToItsOwnNameIsAllowedAndDoesNothing()
    {
        // The UI commits on focus loss, so this is the common case, and treating
        // the clip as colliding with itself would show an error for a no-op.
        var check = ClipRename.Check(Clip("same"), "same", _ => true);

        Assert.True(check.IsAllowed);
        Assert.True(ClipRename.IsNoOp(check.Plan!.Value));
    }

    [Fact]
    public void ARealRenameIsNotANoOp() =>
        Assert.False(ClipRename.IsNoOp(
            ClipRename.Check(Clip("before"), "after", NothingExists).Plan!.Value));

    [Fact]
    public void ApplyingARenameUpdatesBothThePathAndTheDisplayName()
    {
        var clip = Clip("before", game: "cs2", bookmarks: [new Bookmark(5, BookmarkSource.Manual)]);
        var plan = ClipRename.Check(clip, "after", NothingExists).Plan!.Value;

        var renamed = ClipRename.Apply(clip, plan);

        Assert.Equal(@"C:\Videos\Frost\after.mp4", renamed.FilePath);
        Assert.Equal("after", renamed.DisplayName);

        // And nothing else is lost in the copy.
        Assert.Equal("cs2", renamed.GameName);
        Assert.Single(renamed.Bookmarks);
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedFromAnAcceptedName()
    {
        var plan = ClipRename.Check(Clip("before"), "  clutch ace", NothingExists).Plan!.Value;

        Assert.Equal("clutch ace", plan.DisplayName);
        Assert.Equal(@"C:\Videos\Frost\clutch ace.mp4", plan.NewClipPath);
    }

    // ---- trim --------------------------------------------------------------

    private static long Sec(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    /// <summary>Keyframes every two seconds, which is Frost's default interval.</summary>
    private static long[] KeyFrames(int count = 16) =>
        Enumerable.Range(0, count).Select(i => Sec(i * 2)).ToArray();

    [Fact]
    public void TheStartSnapsBackwardsToAKeyFrameSoTheOutputIsNotGarbage()
    {
        // A re-mux copies encoded samples untouched, so it can only begin on a
        // keyframe; starting mid-GOP makes the first second unwatchable.
        var check = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(7.4), Sec(12));

        Assert.True(check.IsAllowed);
        var range = check.Range!.Value;

        Assert.Equal(Sec(6), range.StartTicks);
        Assert.Equal(Sec(12), range.EndTicks);
        Assert.False(range.IsAligned);
        Assert.Equal(1.4, range.StartDrift.TotalSeconds, 3);
    }

    [Fact]
    public void TheStartNeverSnapsForwardsBecauseThatWouldDropTheMoment()
    {
        // 7.4s is closer to the keyframe at 8s than the one at 6s, so a
        // nearest-keyframe snap would cut off the 0.6s the user asked to keep.
        // That is the one thing a trim UI must never do silently.
        var range = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(7.4), Sec(12)).Range!.Value;

        Assert.True(range.StartTicks <= range.RequestedStartTicks);
    }

    [Fact]
    public void AStartAlreadyOnAKeyFrameIsNotMoved()
    {
        var range = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(6), Sec(12)).Range!.Value;

        Assert.Equal(Sec(6), range.StartTicks);
        Assert.True(range.IsAligned);
        Assert.Equal(TimeSpan.Zero, range.StartDrift);
    }

    [Fact]
    public void TheEndIsNotAlignedBecauseADecoderStopsWhereTheSamplesStop()
    {
        var range = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(0), Sec(11.3)).Range!.Value;

        Assert.Equal(Sec(11.3), range.EndTicks);
    }

    [Theory]
    [InlineData(0, 0, TrimRejection.EmptyRange)]
    [InlineData(10, 5, TrimRejection.EmptyRange)]
    [InlineData(-1, 10, TrimRejection.OutOfBounds)]
    [InlineData(0, 31, TrimRejection.OutOfBounds)]
    public void AnImpossibleRangeIsRefusedWithTheReasonNamed(
        double start,
        double end,
        TrimRejection expected)
    {
        var check = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(start), Sec(end));

        Assert.False(check.IsAllowed);
        Assert.Equal(expected, check.Rejection);
        Assert.Null(check.Range);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void ARangeThatKeyFrameAlignmentWouldMakeTooShortIsStillJudgedAfterAlignment()
    {
        // Requested 0.4s, which is under the minimum on its own — but alignment
        // pulls the start back to 6s, making the real output 1.6s. The check has
        // to judge what the re-mux produces, not what was asked for.
        var check = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(7.6), Sec(8));

        Assert.True(check.IsAllowed);
        Assert.Equal(2.0, check.Range!.Value.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void ATrimShorterThanHalfASecondAfterAlignmentIsRefused()
    {
        // Start already on a keyframe, so alignment cannot rescue it.
        var check = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(6), Sec(6.2));

        Assert.Equal(TrimRejection.TooShort, check.Rejection);
    }

    [Fact]
    public void AClipWithNoKeyFrameIndexCannotBeTrimmedLosslessly()
    {
        var check = TrimPlanner.Check(Sec(30), [], Sec(0), Sec(10));

        Assert.Equal(TrimRejection.NoKeyFrames, check.Rejection);
        Assert.Contains("losslessly", check.Message);
    }

    [Fact]
    public void AKeyFrameIndexThatDoesNotCoverTheStartIsRefusedRatherThanGuessed()
    {
        // Every keyframe is after the requested start. Silently starting at the
        // first one would drop the seconds before it with no warning.
        var check = TrimPlanner.Check(Sec(30), [Sec(10), Sec(12)], Sec(2), Sec(14));

        Assert.Equal(TrimRejection.NoKeyFrames, check.Rejection);
    }

    [Fact]
    public void AClipWithNoRecordedLengthIsRefused()
    {
        Assert.Equal(
            TrimRejection.OutOfBounds,
            TrimPlanner.Check(0, KeyFrames(), Sec(0), Sec(5)).Rejection);
    }

    [Fact]
    public void TrimmingTheWholeClipIsAllowed()
    {
        var check = TrimPlanner.Check(Sec(30), KeyFrames(), 0, Sec(30));

        Assert.True(check.IsAllowed);
        Assert.Equal(0, check.Range!.Value.StartTicks);
        Assert.Equal(Sec(30), check.Range.Value.EndTicks);
    }

    [Fact]
    public void TheKeyFrameSearchAgreesWithALinearScanAcrossEveryOffset()
    {
        // Binary search because the trim UI calls this on every drag of the
        // handle; this is the test that it is actually correct.
        var keyFrames = KeyFrames(50);

        for (var ticks = -Sec(1); ticks <= Sec(101); ticks += TimeSpan.TicksPerSecond / 4)
        {
            long? expected = null;

            foreach (var candidate in keyFrames)
            {
                if (candidate <= ticks)
                {
                    expected = candidate;
                }
            }

            Assert.Equal(expected, TrimPlanner.KeyFrameAtOrBefore(keyFrames, ticks));
        }
    }

    [Fact]
    public void TheKeyFrameSearchHandlesAnEmptyIndex() =>
        Assert.Null(TrimPlanner.KeyFrameAtOrBefore([], Sec(5)));

    [Fact]
    public void TheTrimDescriptionExplainsTheDriftRatherThanHidingIt()
    {
        // "Lossless" quietly including two extra seconds looks like a bug;
        // saying so makes it a trade the user can accept.
        var drifted = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(7.4), Sec(12)).Range!.Value;
        var description = TrimPlanner.Describe(drifted);

        Assert.Contains("earlier", description);
        Assert.Contains("lossless", description);

        var exact = TrimPlanner.Check(Sec(30), KeyFrames(), Sec(6), Sec(12)).Range!.Value;
        Assert.Equal("6s", TrimPlanner.Describe(exact));
    }
}
