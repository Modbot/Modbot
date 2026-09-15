using System.Collections.Concurrent;
using System.Net;

namespace Modbot.Cloud.Features.Admin;

/// <summary>
/// Wrong keys per IP address. After <see cref="MaxFailures"/> within <see cref="Window"/>, that address
/// is refused until the window ends, even with the right key.
/// </summary>
/// <remarks>
/// Kept in memory: a restart forgets it, and so would a second copy of the app. A key worth guessing
/// is long enough that five guesses a quarter hour, even reset now and then, gets nowhere.
/// </remarks>
public sealed class LoginAttempts(TimeProvider time)
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);

    private sealed record Failures(int Count, DateTimeOffset WindowStart);

    /// <summary>How long until the address may try again, or null when it may try now.</summary>
    public TimeSpan? WaitFor(IPAddress? ip)
    {
        var now = time.GetUtcNow();

        if (_failures.TryGetValue(Key(ip), out var failures)
            && failures.Count >= MaxFailures
            && failures.WindowStart + Window > now)
        {
            return failures.WindowStart + Window - now;
        }

        return null;
    }

    public void Failed(IPAddress? ip)
    {
        var now = time.GetUtcNow();

        _failures.AddOrUpdate(
            Key(ip),
            _ => new Failures(1, now),
            (_, f) => f.WindowStart + Window <= now ? new Failures(1, now) : f with { Count = f.Count + 1 });

        if (_failures.Count > PruneAbove)
        {
            foreach (var (key, f) in _failures)
            {
                if (f.WindowStart + Window <= now)
                    _failures.TryRemove(key, out _);
            }
        }
    }

    public void Succeeded(IPAddress? ip) => _failures.TryRemove(Key(ip), out _);

    private static string Key(IPAddress? ip) => ip?.ToString() ?? "unknown";
}
