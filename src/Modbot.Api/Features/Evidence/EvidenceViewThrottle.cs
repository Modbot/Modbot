namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Decides whether a look at a piece of evidence is written to the audit log now, or was already
/// written a moment ago.
/// </summary>
/// <remarks>
/// <para>
/// Opening a case file shows every file on it, and a video asks the server for its bytes in
/// pieces every time it is played or skipped through. Written for each request, one person
/// watching one clip would fill the log with a hundred identical lines and bury the ones that say
/// who looked at what. So a look is written at most once per person per file every
/// <see cref="Window"/>. Downloading is not thinned out: it does not come through here.
/// </para>
/// <para>
/// Held in memory rather than read back from the log on every request, because it is asked on
/// every range request of a playing video. A restart forgets it, which costs at most one extra
/// line per person and file, never a missing one.
/// </para>
/// </remarks>
public sealed class EvidenceViewThrottle
{
    /// <summary>How long a written look silences the next ones from the same person for the same file.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private const int PruneAbove = 10_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid Person, string Hash), DateTimeOffset> _lastWritten = [];

    /// <summary>
    /// True when this look should be written: nobody's look at this file was written in the last
    /// <see cref="Window"/> by this person. Claims the slot when it is.
    /// </summary>
    public bool TryClaim(Guid person, string hash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);

        lock (_lock)
        {
            if (_lastWritten.TryGetValue((person, hash), out var last) && now - last < Window)
                return false;

            _lastWritten[(person, hash)] = now;

            if (_lastWritten.Count > PruneAbove)
            {
                foreach (var stale in _lastWritten.Where(pair => now - pair.Value >= Window).Select(pair => pair.Key).ToList())
                    _lastWritten.Remove(stale);
            }

            return true;
        }
    }
}
