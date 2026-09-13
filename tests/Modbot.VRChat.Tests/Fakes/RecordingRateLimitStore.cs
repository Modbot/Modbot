using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// An in-memory stand-in for the database store, shared across "restarts".
/// </summary>
/// <remarks>
/// Handing the same instance to a second limiter is what a redeploy looks like from the limiter's
/// point of view: new process, same persisted state. The database-backed equivalent of this is
/// exercised separately against real PostgreSQL, because that is where the state actually lives.
/// </remarks>
public sealed class RecordingRateLimitStore : IRateLimitStore
{
    private IReadOnlyList<RateLimitBucketRecord> _records = [];

    public int Saves { get; private set; }

    public int Loads { get; private set; }

    /// <summary>The last written state, as a fresh limiter would read it.</summary>
    public IReadOnlyList<RateLimitBucketRecord> Records => _records;

    public RateLimitBucketRecord? this[string name] =>
        _records.FirstOrDefault(r => r.Name == name);

    public Task<IReadOnlyList<RateLimitBucketRecord>> LoadAsync(CancellationToken ct = default)
    {
        Loads++;
        return Task.FromResult(_records);
    }

    public Task SaveAsync(IReadOnlyList<RateLimitBucketRecord> buckets, CancellationToken ct = default)
    {
        Saves++;
        _records = [.. buckets];

        return Task.CompletedTask;
    }
}
