using Modbot.Companion.Clips;

namespace Modbot.Companion.App;

/// <summary>
/// Hands the part that answers clip requests the recorder as it is now. The recorder comes and goes
/// as Clips is switched on and off, so this asks for it each time instead of holding one.
/// </summary>
/// <remarks>
/// It does nothing the recorder does not already do for the moderator's own clips: it lists the
/// recordings being written and asks for one to be closed and moved. It touches no screen and no
/// window.
/// </remarks>
internal sealed class RecorderClipSource(Func<ScreenRecording?> recorder) : IClipSource
{
    public bool IsRecording => recorder() is { IsRecording: true, AnyPictureTaken: true };

    public long BitsPerSecond => ScreenRecording.BitsPerSecond;

    public IReadOnlyList<RecordingSpan> Spans() => recorder()?.Spans() ?? [];

    public Task<SavedRecording?> CopyAsync(string rollingPath, string destination, CancellationToken ct) =>
        recorder() is { } current
            ? current.CopyAsync(rollingPath, destination, ct)
            : Task.FromResult<SavedRecording?>(null);
}
