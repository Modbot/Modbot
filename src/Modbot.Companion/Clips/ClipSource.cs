namespace Modbot.Companion.Clips;

/// <summary>A recording taken out of the recorder, closed, as a file of its own.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="StartedAt">When its first picture was taken.</param>
/// <param name="Length">How long it runs.</param>
/// <param name="Bytes">The file's size.</param>
public sealed record SavedRecording(string Path, DateTimeOffset StartedAt, TimeSpan Length, long Bytes);

/// <summary>
/// What the recorder offers the part that answers clip requests. The Windows one lives in the app,
/// beside the recorder; this library only knows the shape.
/// </summary>
public interface IClipSource
{
    /// <summary>Whether Clips is on and recording right now.</summary>
    bool IsRecording { get; }

    /// <summary>The recorder's picture and sound together, in bits a second.</summary>
    long BitsPerSecond { get; }

    /// <summary>The recordings being written now: the file, when it began and how long it runs so far.</summary>
    IReadOnlyList<RecordingSpan> Spans();

    /// <summary>
    /// Closes the recording being written at <paramref name="rollingPath"/> and moves it to
    /// <paramref name="destination"/>; the recorder starts a fresh one. Null when that recording is
    /// no longer there or could not be moved. This is a separate request from the moderator's own
    /// Save a clip and leaves what that shows alone.
    /// </summary>
    Task<SavedRecording?> CopyAsync(string rollingPath, string destination, CancellationToken ct);
}
