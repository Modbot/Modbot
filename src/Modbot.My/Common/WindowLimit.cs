using System.Collections.Concurrent;

namespace Modbot.My.Common;

/// <summary>
/// At most <c>limit</c> of something per key in each fixed window of time.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory. A restart forgets it, and a second copy of the service would keep its own. These
/// are limits against a runaway or hostile caller, not accounting, and that is enough for them.
/// </para>
/// <para>
/// Fixed windows rather than a sliding one: a caller can send up to twice the limit across a window
/// boundary. The limits are set far above what a person browsing does, so that edge does not matter,
/// and the answer to "when may I try again" is exact, which is what <c>Retry-After</c> needs.
/// </para>
/// </remarks>
public sealed class WindowLimit(int limit, TimeSpan window, TimeProvider time)
{
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    private sealed class Counter(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public long Used { get; set; }
    }

    public int Limit => limit;

    /// <summary>
    /// Takes one from the key's allowance. Returns null when it fit, or how long until the window
    /// resets when it did not — in which case nothing was taken.
    /// </summary>
    public TimeSpan? TryTake(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var now = time.GetUtcNow();
        var counter = _counters.GetOrAdd(key, _ => new Counter(now));

        TimeSpan? wait;
        lock (counter)
        {
            if (counter.WindowStart + window <= now)
            {
                counter.WindowStart = now;
                counter.Used = 0;
            }

            if (counter.Used + 1 > limit)
            {
                wait = counter.WindowStart + window - now;
            }
            else
            {
                counter.Used++;
                wait = null;
            }
        }

        if (_counters.Count > PruneAbove)
        {
            foreach (var (existing, c) in _counters)
            {
                if (c.WindowStart + window <= now)
                    _counters.TryRemove(existing, out _);
            }
        }

        return wait;
    }
}

/// <summary>
/// What one IP address may ask my.modbot.co to do in an hour.
/// </summary>
/// <remarks>
/// These exist so that one address cannot make my.modbot.co hammer Modbot Cloud. They are far above
/// what a person opening pages does: a page view costs one save, and the list is read once a page.
/// </remarks>
public sealed class SiteLimits(TimeProvider time)
{
    public const int SavesPerHour = 30;

    public const int ReadsPerHour = 60;

    public WindowLimit Saves { get; } = new(SavesPerHour, TimeSpan.FromHours(1), time);

    public WindowLimit Reads { get; } = new(ReadsPerHour, TimeSpan.FromHours(1), time);
}
