using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frost.Shared.Clips;

/// <summary>
/// Source-generated JSON for everything that crosses a process or file boundary.
/// </summary>
/// <remarks>
/// Source generation rather than reflection because the Engine is AOT-compiled:
/// reflection-based serialisation either fails outright or drags in the whole
/// reflection stack, and neither is acceptable in a process with a 50MB budget.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClipMetadata))]
[JsonSerializable(typeof(Bookmark))]
[JsonSerializable(typeof(List<ClipMetadata>))]
public partial class FrostClipJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the sidecar file that sits next to each recording.
/// </summary>
/// <remarks>
/// A sidecar, not metadata embedded in the MP4: a rename or a new bookmark then
/// rewrites a 1KB JSON file instead of remuxing a 200MB video, and a clip whose
/// sidecar is lost or corrupt still plays. Every read is defensive for exactly
/// that reason — a bad sidecar must degrade the gallery entry, never hide the
/// clip.
/// </remarks>
public static class ClipMetadataStore
{
    /// <summary>Writes the sidecar for a clip.</summary>
    public static void Save(ClipMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var path = ClipMetadata.SidecarPathFor(metadata.FilePath);
        var json = JsonSerializer.Serialize(metadata, FrostClipJsonContext.Default.ClipMetadata);

        // Write to a temporary file and move it into place, so an interrupted
        // write cannot leave a half-written sidecar behind.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Reads the sidecar for a clip, or null when it is absent or unreadable.
    /// </summary>
    public static ClipMetadata? TryLoad(string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        var path = ClipMetadata.SidecarPathFor(clipPath);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllText(path), FrostClipJsonContext.Default.ClipMetadata);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Metadata for a clip, falling back to what the file itself can tell us
    /// when the sidecar is missing.
    /// </summary>
    public static ClipMetadata Describe(string clipPath, ClipKind kind = ClipKind.InstantClip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        var loaded = TryLoad(clipPath);
        if (loaded is not null)
        {
            return loaded;
        }

        var info = new FileInfo(clipPath);

        return new ClipMetadata
        {
            FilePath = clipPath,
            DisplayName = Path.GetFileNameWithoutExtension(clipPath),
            CreatedUtc = info.Exists ? info.CreationTimeUtc : DateTimeOffset.UtcNow,
            DurationTicks = 0,
            SizeBytes = info.Exists ? info.Length : 0,
            Width = 0,
            Height = 0,
            Codec = "unknown",
            Kind = kind,
        };
    }

    /// <summary>Deletes a clip and its sidecar.</summary>
    public static void Delete(string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        var sidecar = ClipMetadata.SidecarPathFor(clipPath);

        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }

        if (File.Exists(clipPath))
        {
            File.Delete(clipPath);
        }
    }
}
