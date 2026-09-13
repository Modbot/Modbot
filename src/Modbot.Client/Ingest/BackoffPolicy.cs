namespace Modbot.Client.Ingest;

/// <summary>
/// How long to wait before retrying a failed send.
/// </summary>
/// <remarks>
/// <para><strong>This is ordinary exponential backoff, and that is a deliberate difference from
/// how Modbot treats VRChat.</strong> Foundation 4.3.1 forbids retrying VRChat's <c>429</c> at
/// all — a cold stop — because VRChat's limiter is punitive and opaque, and retrying <em>extends</em>
/// the penalty. None of that reasoning applies here. A Modbot server is ordinary software under the
/// group operator's own control, it sends <c>Retry-After</c> when it wants a pause, and the cost of
/// not retrying is lost presence history that cannot be backfilled.</para>
/// <para><strong>Do not copy the cold-stop logic across.</strong> The two rules look similar and
/// are opposites, and the client sits next to code that implements the other one.</para>
/// <para>Jitter matters more here than it usually does: several moderators in one instance see the
/// same server outage at the same moment, and unjittered backoff would have them all retry in
/// lockstep for as long as it lasted.</para>
/// </remarks>
public sealed class BackoffPolicy
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _ceiling;
    private readonly double _multiplier;
    private readonly Func<double> _jitter;

    public BackoffPolicy(
        TimeSpan? initial = null,
        TimeSpan? ceiling = null,
        double multiplier = 2.0,
        Func<double>? jitter = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(2);
        _ceiling = ceiling ?? TimeSpan.FromMinutes(5);
        _multiplier = multiplier;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <summary>Protocol 4.4: capped at about five minutes.</summary>
    public TimeSpan Ceiling => _ceiling;

    /// <summary>
    /// The delay after <paramref name="consecutiveFailures"/> failures in a row. Half of the window
    /// is fixed and half is random, so retries spread out without any client ever hammering.
    /// </summary>
    public TimeSpan Delay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.Zero;

        var uncapped = _initial.TotalMilliseconds * Math.Pow(_multiplier, consecutiveFailures - 1);
        var window = Math.Min(uncapped, _ceiling.TotalMilliseconds);

        return TimeSpan.FromMilliseconds((window / 2) + (_jitter() * window / 2));
    }
}
