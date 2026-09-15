using System.Collections.Concurrent;

namespace Modbot.Cloud.Common;

/// <summary>
/// At most <c>limit</c> of something per key in each fixed window of time: registrations per IP
/// address, batches per install, events per install.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory. A restart forgets it, and a second copy of Cloud would keep its own. These are
/// limits against a runaway or hostile client, not accounting, and that is enough for them.
/// </para>
/// <para>
/// Fixed windows rather than a sliding one: a client can send up to twice the limit across a window
/// boundary. The limits are set far above what a real client sends, so that edge does not matter,
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
    /// Takes <paramref name="amount"/> from the key's allowance. Returns null when it fit, or how long
    /// until the window resets when it did not — in which case nothing was taken.
    /// </summary>
    public TimeSpan? TryTake(string key, int amount = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

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

            if (counter.Used + amount > limit)
            {
                wait = counter.WindowStart + window - now;
            }
            else
            {
                counter.Used += amount;
                wait = null;
            }
        }

        if (_counters.Count > PruneAbove)
            Prune(now);

        return wait;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (counter.WindowStart + window <= now)
                _counters.TryRemove(key, out _);
        }
    }
}
