using System.Collections.Concurrent;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Auth.Login;

/// <summary>
/// Makes repeated attempts at something slow, without ever locking anybody out.
/// </summary>
/// <remarks>
/// <para>
/// Accounts and access design §7. Attempts are counted per normalised username and per client
/// address over a fifteen-minute window; the wait before the next attempt doubles per failure and
/// is capped. A correct password always works after the wait, so an attacker hammering
/// <c>owner</c> cannot keep the owner out -- the worst they can do is make the owner wait twenty
/// seconds. A lockout would hand them exactly the lever this refuses to provide.
/// </para>
/// <para>
/// In memory, per process. Modbot is a single-instance appliance (foundation §2.4) and the count
/// is a nuisance to an attacker rather than a record; the record is the <c>LoginFailed</c> fact.
/// </para>
/// <para>
/// <strong>Old counts are swept out.</strong> An entry is made per username tried and per address
/// it was tried from, and until 2026-09-24 nothing ever removed one that was not tried again: a
/// spread-out guessing run left a row per address for as long as the process ran, which on a
/// small machine is the sort of slow fill that shows up a fortnight later as an out-of-memory
/// kill. Everything past <see cref="Window"/> is now swept out, but only once there have been
/// at least as many attempts since the last sweep as there are counts to walk. That keeps the
/// work per attempt flat however the attempts are shaped — a flood of genuinely recent keys
/// cannot make every attempt walk the whole store — and it bounds the store at roughly twice
/// what was seen inside the window, rather than at everything ever seen.
/// </para>
/// </remarks>
public abstract class AttemptSlowdown
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);

    /// <summary>The fewest attempts between sweeps. A quiet Modbot never sweeps at all.</summary>
    private const int SweepAfter = 1024;

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Last)> _entries = new();
    private readonly IModbotClock _clock;

    /// <summary>Attempts recorded since the last sweep.</summary>
    private int _sinceSweep;

    protected AttemptSlowdown(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>How many counts are being held. For the tests.</summary>
    public int Held => _entries.Count;

    /// <summary>How long this attempt should wait before it is acted on.</summary>
    public TimeSpan WaitFor(string? username, string? address)
    {
        var failures = Math.Max(FailuresFor(UserKey(username)), FailuresFor(AddressKey(address)));

        return WaitFor(failures);
    }

    /// <summary>The wait after <paramref name="failures"/> recent failures: 0, 1, 2, 4, 8, 16, 20, 20…</summary>
    public static TimeSpan WaitFor(int failures)
    {
        if (failures <= 0) return TimeSpan.Zero;

        var seconds = Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
    }

    public void RecordFailure(string? username, string? address)
    {
        var now = _clock.UtcNow;

        foreach (var key in new[] { UserKey(username), AddressKey(address) })
        {
            _entries.AddOrUpdate(
                key,
                _ => (1, now),
                (_, existing) => now - existing.Last > Window ? (1, now) : (existing.Failures + 1, now));
        }

        SweepIfDue(now);
    }

    /// <summary>
    /// Removes every count older than <see cref="Window"/>, once enough attempts have gone by to
    /// pay for the walk.
    /// </summary>
    /// <remarks>
    /// Waiting for the attempts rather than for the size is what keeps this honest. Sweeping
    /// whenever the store is big would walk the whole store on every attempt as soon as a flood
    /// of genuinely recent keys sat above the line — the sweep would become the attack. Waiting
    /// until there have been as many attempts as there are counts spreads one walk over that many
    /// attempts, so the work each attempt does is flat whatever an attacker tries.
    /// </remarks>
    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Math.Max(SweepAfter, _entries.Count);
        if (Interlocked.Increment(ref _sinceSweep) < due)
            return;

        Interlocked.Exchange(ref _sinceSweep, 0);

        foreach (var (key, entry) in _entries)
        {
            if (now - entry.Last > Window)
                _entries.TryRemove(key, out _);
        }
    }

    /// <summary>A success clears the count for both keys: the person is who they said.</summary>
    public void RecordSuccess(string? username, string? address)
    {
        _entries.TryRemove(UserKey(username), out _);
        _entries.TryRemove(AddressKey(address), out _);
    }

    private int FailuresFor(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return 0;

        if (_clock.UtcNow - entry.Last > Window)
        {
            _entries.TryRemove(key, out _);
            return 0;
        }

        return entry.Failures;
    }

    private static string UserKey(string? username)
        => "u:" + (string.IsNullOrWhiteSpace(username) ? string.Empty : UserAccountService.Normalize(username));

    private static string AddressKey(string? address) => "a:" + (address ?? string.Empty);
}

/// <summary>Failed sign-ins.</summary>
public sealed class LoginSlowdown : AttemptSlowdown
{
    public LoginSlowdown(IModbotClock clock) : base(clock) { }
}

/// <summary>
/// Forgot-password requests. Its own counter, so asking for a reset link does not slow down the
/// sign-in that follows it, and so a flood of requests cannot make Modbot send a flood of messages.
/// </summary>
public sealed class ForgotPasswordSlowdown : AttemptSlowdown
{
    public ForgotPasswordSlowdown(IModbotClock clock) : base(clock) { }
}
