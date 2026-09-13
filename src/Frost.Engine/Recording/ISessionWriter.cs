namespace Frost.Engine.Recording;

/// <summary>
/// A file being written from already-encoded samples.
/// </summary>
/// <remarks>
/// The seam between full-session recording and the container writer, so the
/// recorder's behaviour — keyframe alignment, timestamp rebasing, bookmark
/// offsets, disk failure handling — is testable without Media Foundation.
/// </remarks>
public interface ISessionWriter : IDisposable
{
    /// <summary>Container extension, with the dot.</summary>
    string Extension { get; }

    /// <summary>Bytes committed so far.</summary>
    long BytesWritten { get; }

    /// <summary>True once the writer has failed; no further samples are accepted.</summary>
    bool IsFaulted { get; }

    /// <summary>
    /// Queues a sample. Must not block on disk — the caller is the encode thread.
    /// Returns false when the sample could not be taken.
    /// </summary>
    bool TryWrite(ReadOnlySpan<byte> data, long timestampTicks, long durationTicks, bool isKeyFrame);

    /// <summary>Flushes and finalises the file. Called off the encode thread.</summary>
    void Finish(TimeSpan timeout);
}
