using Frost.Shared.Clips;

namespace Frost.Shared.Shell;

/// <summary>How the gallery grid is ordered.</summary>
public enum GallerySort
{
    /// <summary>Most recent first. The default, and what people want.</summary>
    NewestFirst = 0,

    OldestFirst = 1,

    /// <summary>Largest first — the order for freeing disk space.</summary>
    LargestFirst = 2,

    LongestFirst = 3,

    NameAscending = 4,
}

/// <summary>What the gallery is currently showing.</summary>
/// <param name="Sort">Grid order.</param>
/// <param name="Search">Free-text match against name and game. Null for none.</param>
/// <param name="Kind">Restrict to one kind of recording. Null for all.</param>
/// <param name="Game">Restrict to one game. Null for all.</param>
/// <param name="BookmarkedOnly">Only recordings with at least one bookmark.</param>
public readonly record struct GalleryFilter(
    GallerySort Sort = GallerySort.NewestFirst,
    string? Search = null,
    ClipKind? Kind = null,
    string? Game = null,
    bool BookmarkedOnly = false);

/// <summary>
/// Orders and filters the clip gallery.
/// </summary>
/// <remarks>
/// Portable so it can be tested here rather than through a WinUI
/// <c>CollectionViewSource</c>, and because the behaviour worth pinning down is
/// not the grid — it is that a search matching nothing is distinguishable from
/// an empty clips folder, that sorting is stable so thumbnails do not shuffle
/// under the cursor when two clips share a timestamp, and that deleting the
/// currently selected clip leaves the selection somewhere sensible.
/// </remarks>
public static class GalleryView
{
    /// <summary>Applies a filter and a sort.</summary>
    public static IReadOnlyList<ClipMetadata> Apply(
        IReadOnlyList<ClipMetadata> clips,
        GalleryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(clips);

        IEnumerable<ClipMetadata> query = clips;

        if (filter.Kind is { } kind)
        {
            query = query.Where(c => c.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(filter.Game))
        {
            query = query.Where(c =>
                string.Equals(c.GameName, filter.Game, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.BookmarkedOnly)
        {
            query = query.Where(c => c.Bookmarks.Count > 0);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();

            query = query.Where(c =>
                c.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (c.GameName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // OrderBy is a stable sort, which matters here: two clips saved in the
        // same second would otherwise swap places on every refresh and the
        // thumbnails visibly shuffle under the cursor.
        return filter.Sort switch
        {
            GallerySort.NewestFirst => query.OrderByDescending(c => c.CreatedUtc).ToList(),
            GallerySort.OldestFirst => query.OrderBy(c => c.CreatedUtc).ToList(),
            GallerySort.LargestFirst => query.OrderByDescending(c => c.SizeBytes).ToList(),
            GallerySort.LongestFirst => query.OrderByDescending(c => c.DurationTicks).ToList(),
            GallerySort.NameAscending => query
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => query.ToList(),
        };
    }

    /// <summary>
    /// The games present in a clip set, for the filter dropdown.
    /// </summary>
    public static IReadOnlyList<string> Games(IReadOnlyList<ClipMetadata> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        return clips
            .Select(c => c.GameName)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Total bytes of a clip set, for the storage readout.</summary>
    public static long TotalBytes(IReadOnlyList<ClipMetadata> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var total = 0L;

        foreach (var clip in clips)
        {
            // Saturating rather than wrapping: a corrupt sidecar reporting
            // long.MaxValue must not make the storage readout negative.
            total = clip.SizeBytes > long.MaxValue - total ? long.MaxValue : total + clip.SizeBytes;
        }

        return total;
    }

    /// <summary>
    /// Why the grid is empty, so the UI can say the right thing.
    /// </summary>
    /// <remarks>
    /// "No clips yet — press Alt+F10 while playing" and "Nothing matches
    /// 'shroud'" are completely different messages, and showing the first one to
    /// someone with four hundred clips and a typo in the search box is the kind
    /// of small wrongness that makes an app feel careless.
    /// </remarks>
    public static GalleryEmptyReason EmptyReason(
        IReadOnlyList<ClipMetadata> allClips,
        IReadOnlyList<ClipMetadata> shown,
        GalleryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(allClips);
        ArgumentNullException.ThrowIfNull(shown);

        if (shown.Count > 0)
        {
            return GalleryEmptyReason.NotEmpty;
        }

        if (allClips.Count == 0)
        {
            return GalleryEmptyReason.NoClipsAtAll;
        }

        return HasAnyFilter(filter)
            ? GalleryEmptyReason.FilteredOut
            : GalleryEmptyReason.NotEmpty;
    }

    private static bool HasAnyFilter(GalleryFilter filter) =>
        !string.IsNullOrWhiteSpace(filter.Search)
        || filter.Kind is not null
        || !string.IsNullOrWhiteSpace(filter.Game)
        || filter.BookmarkedOnly;

    /// <summary>
    /// What to select after removing a clip from the grid.
    /// </summary>
    /// <remarks>
    /// Deleting the clip under the cursor and landing on nothing means the next
    /// delete needs a fresh click, which is miserable when clearing out a
    /// session. Selecting the next clip down — or the previous one when the
    /// deleted clip was last — keeps a run of deletes going.
    /// </remarks>
    public static int SelectionAfterRemoving(int removedIndex, int countBeforeRemoval)
    {
        if (countBeforeRemoval <= 1 || removedIndex < 0 || removedIndex >= countBeforeRemoval)
        {
            return -1;
        }

        var countAfter = countBeforeRemoval - 1;
        return removedIndex >= countAfter ? countAfter - 1 : removedIndex;
    }
}

/// <summary>Why the gallery has nothing to show.</summary>
public enum GalleryEmptyReason
{
    NotEmpty = 0,

    /// <summary>Nothing has ever been recorded.</summary>
    NoClipsAtAll = 1,

    /// <summary>There are clips, but the filter excludes all of them.</summary>
    FilteredOut = 2,
}
