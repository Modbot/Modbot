namespace Modbot.Api.Features.DiscordLink;

/// <summary>
/// At most <see cref="PerMinute"/> bio checks a minute from the link page, across the whole
/// deployment (Discord account linking design §3.1).
/// </summary>
/// <remarks>
/// The per-account limits stop one person pressing Check over and over; this stops many new Discord
/// accounts doing it together, since anyone can make one. It sits in front of the VRChat request, so
/// a refused check costs no VRChat budget. Held in memory: a restart forgets it, which at worst
/// allows one extra minute's worth.
/// </remarks>
public sealed class LinkCheckLimit
{
    public const int PerMinute = 30;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly Lock _gate = new();

    /// <summary>Counts one check at <paramref name="now"/>, or returns false when the minute is full.</summary>
    public bool TryTake(DateTimeOffset now)
    {
        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() >= Window)
                _recent.Dequeue();

            if (_recent.Count >= PerMinute)
                return false;

            _recent.Enqueue(now);
            return true;
        }
    }
}
