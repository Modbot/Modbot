using System.Text.Json.Serialization;

namespace Modbot.VRChat.Session;

/// <summary>Why Modbot is not signing in to VRChat right now (spec 4.1.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignInWaitReason>))]
public enum SignInWaitReason
{
    /// <summary>VRChat answered a sign-in with a rate limit. Modbot waits an hour from that moment.</summary>
    RateLimitedByVRChat = 1,

    /// <summary>
    /// Modbot's own limit of sign-ins per rolling hour is used up. Modbot waits until the oldest
    /// one is an hour old. Still a rate limit, and shown as one.
    /// </summary>
    SignInLimitReached = 2,
}

/// <summary>A wait before the next sign-in, and when it ends.</summary>
public sealed record SignInWait(SignInWaitReason Reason, DateTimeOffset RetryAt);

/// <summary>What the gate would tell an operator about signing in (spec 4.1.2).</summary>
/// <param name="State">The gate's state, <see cref="VRChatSessionState.SignInWaiting"/> while waiting.</param>
/// <param name="Wait">The wait in force, or null.</param>
/// <param name="LastSignedInAt">When Modbot last signed in with the password.</param>
/// <param name="SignInsInLastHour">Requests counted against the limit in the last hour.</param>
/// <param name="SignInLimit">The limit per rolling hour.</param>
/// <param name="Now">The server's clock when this was read, so a countdown need not trust the browser's.</param>
public sealed record SignInStatus(
    VRChatSessionState State,
    SignInWait? Wait,
    DateTimeOffset? LastSignedInAt,
    int SignInsInLastHour,
    int SignInLimit,
    DateTimeOffset Now);

/// <summary>What was stored about signing in, as read on start-up.</summary>
/// <param name="Attempts">When each counted request in the window was sent, oldest first.</param>
public sealed record StoredSignIns(
    IReadOnlyList<DateTimeOffset> Attempts,
    SignInWait? Wait,
    DateTimeOffset? LastSignedInAt);

/// <summary>
/// The sign-in limit, as a pure function of the counted requests and the clock.
/// </summary>
/// <remarks>
/// <para>
/// VRChat allows about four or five sign-ins an hour, publishes nothing about it, and answers the
/// next one with an hour-long block. So Modbot counts every request that could be a sign-in and
/// keeps to at most <see cref="RateLimiting.RateLimitOptions.MaxSignInsPerHour"/> in any rolling
/// hour, below what VRChat allows rather than at it.
/// </para>
/// <para>
/// A sign-in that cannot finish inside the room left is not started. A two-factor sign-in is three
/// counted requests, and stopping after the first would spend one and gain nothing.
/// </para>
/// </remarks>
public static class SignInBudget
{
    /// <summary>The window the limit counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>How long a rate limit on a sign-in stops every sign-in, from the moment it arrived.</summary>
    public static readonly TimeSpan RateLimitWait = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether <paramref name="needed"/> more counted requests fit, and if not, when they will.
    /// </summary>
    /// <returns>Null when they fit now.</returns>
    public static SignInWait? Check(
        IReadOnlyList<DateTimeOffset> attempts, int limit, int needed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        if (limit < 1)
            limit = 1;

        // Asking for more than the limit can ever hold would wait forever; ask for the whole limit.
        needed = Math.Clamp(needed, 1, limit);

        var recent = attempts
            .Where(at => at > now - Window)
            .OrderBy(at => at)
            .ToList();

        var over = recent.Count + needed - limit;
        if (over <= 0)
            return null;

        // The request that has to age out is the over-th oldest: once it is an hour old, enough
        // room has come back for the whole sign-in.
        return new SignInWait(SignInWaitReason.SignInLimitReached, recent[over - 1] + Window);
    }

    /// <summary>How many counted requests are inside the window.</summary>
    public static int InWindow(IReadOnlyList<DateTimeOffset> attempts, DateTimeOffset now) =>
        attempts.Count(at => at > now - Window);
}

/// <summary>
/// Where the sign-in limit's record lives across restarts.
/// </summary>
/// <remarks>
/// Spec 4.1.2. The shipped implementation is the database; a restart, a crash loop or a Railway
/// redeploy must not be able to hand the allowance back or cut a wait short. The interface exists
/// so the gate can be tested without one.
/// </remarks>
public interface IVRChatSignInStore
{
    /// <summary>Reads the counted requests since <paramref name="since"/>, the wait, and the last sign-in.</summary>
    Task<StoredSignIns> LoadAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Records one request that could count as a sign-in. Called before it is sent.</summary>
    Task RecordAttemptAsync(DateTimeOffset at, string operation, CancellationToken ct = default);

    /// <summary>Stores the wait in force, or clears it.</summary>
    Task SaveWaitAsync(SignInWait? wait, CancellationToken ct = default);

    /// <summary>Records a successful sign-in with the password.</summary>
    Task RecordSignedInAsync(DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>
/// The sign-in record without a database, for tests and for hosts that have none.
/// </summary>
/// <remarks>
/// Not what production uses: a restart forgets it, which is exactly what spec 4.1.2 forbids. Share
/// one instance between two gates to reproduce a restart that keeps its state.
/// </remarks>
public sealed class MemorySignInStore : IVRChatSignInStore
{
    private readonly Lock _lock = new();
    private readonly List<(DateTimeOffset At, string Operation)> _attempts = [];
    private SignInWait? _wait;
    private DateTimeOffset? _lastSignedInAt;

    /// <summary>Every recorded request, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, string Operation)> Attempts
    {
        get
        {
            lock (_lock)
                return [.. _attempts];
        }
    }

    public SignInWait? Wait
    {
        get
        {
            lock (_lock)
                return _wait;
        }
    }

    public DateTimeOffset? LastSignedInAt
    {
        get
        {
            lock (_lock)
                return _lastSignedInAt;
        }
    }

    public Task<StoredSignIns> LoadAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(new StoredSignIns(
                [.. _attempts.Where(a => a.At > since).Select(a => a.At).Order()],
                _wait,
                _lastSignedInAt));
        }
    }

    public Task RecordAttemptAsync(DateTimeOffset at, string operation, CancellationToken ct = default)
    {
        lock (_lock)
            _attempts.Add((at, operation));

        return Task.CompletedTask;
    }

    public Task SaveWaitAsync(SignInWait? wait, CancellationToken ct = default)
    {
        lock (_lock)
            _wait = wait;

        return Task.CompletedTask;
    }

    public Task RecordSignedInAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_lock)
            _lastSignedInAt = at;

        return Task.CompletedTask;
    }
}
