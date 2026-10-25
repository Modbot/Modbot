namespace Modbot.Companion.Clips;

/// <summary>
/// Writes a shorter copy of a saved recording, for sending as the answer to a clip request.
/// </summary>
/// <remarks>
/// The Windows one lives in the app, beside the recorder, because it needs the same video library.
/// This library only knows the shape, so the rules that decide what to cut can be tested without a
/// screen.
/// </remarks>
public interface IClipCutter
{
    /// <summary>
    /// Copies the part of <paramref name="sourcePath"/> from <paramref name="start"/> to
    /// <paramref name="end"/> (both measured from the start of that file) into
    /// <paramref name="destinationPath"/>. The copy may start a little before
    /// <paramref name="start"/>, on the nearest earlier keyframe. It never writes more than
    /// <paramref name="maxBytes"/>; if it would, it fails instead.
    /// </summary>
    Task<ClipCut> CutAsync(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        long maxBytes,
        string destinationPath,
        CancellationToken ct);
}

/// <summary>What a cut produced.</summary>
/// <param name="Succeeded">Whether a file that opens was written.</param>
/// <param name="Start">Where the copy really starts in the source (a keyframe at or before the start asked for).</param>
/// <param name="Length">How long the copy is, read back from the written file.</param>
/// <param name="Bytes">The written file's size.</param>
/// <param name="Failure">Why it failed, in a few plain words. Null when it worked.</param>
public sealed record ClipCut(bool Succeeded, TimeSpan Start, TimeSpan Length, long Bytes, string? Failure);
