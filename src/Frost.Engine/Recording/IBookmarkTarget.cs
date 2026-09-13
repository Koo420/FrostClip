using Frost.Shared.Clips;

namespace Frost.Engine.Recording;

/// <summary>Something that can have a moment marked in it.</summary>
public interface IBookmarkTarget
{
    /// <summary>
    /// Marks the current moment. Returns false with a reason when there is
    /// nothing to mark.
    /// </summary>
    bool TryAddBookmark(BookmarkSource source, string? label, out string? error);
}
