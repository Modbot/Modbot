using Modbot.TestSupport;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A model of VRChat's rate limiter, including the property that makes it dangerous.
/// </summary>
/// <remarks>
/// <para>
/// An ordinary fake — one that returns 429 while over the limit and 200 once you have waited —
/// makes retrying look free, and every recovery strategy passes against it. Spec 4.3 records that
/// the real limiter is not like that: <strong>a request issued during a penalty extends the
/// penalty</strong>, by roughly 45–80 seconds. That single property is what rules out exponential
/// backoff and forces the cold stop, so it is the property the fake has to have.
/// </para>
/// <para>
/// Limits are per endpoint class, because the real ones are (spec 4.3), and the allowance is a
/// sliding window rather than a token bucket so that the fake does not simply mirror the
/// implementation it is testing.
/// </para>
/// </remarks>
public sealed class PunitiveVRChat(FakeClock clock)
{
    private readonly Dictionary<string, List<DateTimeOffset>> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _penalties = new(StringComparer.Ordinal);

    /// <summary>How long a fresh penalty lasts. The real one is opaque; ten minutes is plausible.</summary>
    public TimeSpan PenaltyDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>What a request issued during a penalty adds to it (spec 4.3: 45–80 seconds).</summary>
    public TimeSpan PenaltyExtension { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Requests allowed per <see cref="Window"/>, per endpoint class.</summary>
    public int AllowancePerWindow { get; init; } = 5;

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Every call the fake saw, in order, with the status it returned.</summary>
    public List<(string EndpointClass, DateTimeOffset At, int Status)> Calls { get; } = [];

    public int CallCount(string endpointClass) =>
        Calls.Count(c => c.EndpointClass == endpointClass);

    public int RateLimitedCount => Calls.Count(c => c.Status == 429);

    /// <summary>When the class's penalty expires, for tests that assert on the fake itself.</summary>
    public DateTimeOffset? PenaltyUntil(string endpointClass) =>
        _penalties.TryGetValue(endpointClass, out var until) ? until : null;

    public int Call(string endpointClass)
    {
        var now = clock.UtcNow;
        var status = Evaluate(endpointClass, now);
        Calls.Add((endpointClass, now, status));

        return status;
    }

    private int Evaluate(string endpointClass, DateTimeOffset now)
    {
        if (_penalties.TryGetValue(endpointClass, out var until) && now < until)
        {
            // The whole point. Probing an active penalty makes it worse, so a strategy that
            // converges by probing converges on a longer outage.
            _penalties[endpointClass] = until + PenaltyExtension;
            return 429;
        }

        if (!_requests.TryGetValue(endpointClass, out var times))
            _requests[endpointClass] = times = [];

        times.RemoveAll(t => t <= now - Window);
        times.Add(now);

        if (times.Count > AllowancePerWindow)
        {
            _penalties[endpointClass] = now + PenaltyDuration;
            return 429;
        }

        return 200;
    }
}
