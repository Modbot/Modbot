using Modbot.Core.Time;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Decides how long to wait before the next audit-log poll, from what the last one found.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2 gives <c>groups.auditlog</c> one request per 8 seconds and spec 4.2.3 protects it
/// when the ceiling binds, because it is cheap and it is the authoritative fact source. Neither
/// says to <em>spend</em> that allowance on a group where nothing is happening. Most groups are
/// quiet most of the time, and a fixed 8-second poll would issue ten thousand requests a day to
/// discover that ten thousand times.
/// </para>
/// <para>
/// So: fast while entries are arriving, geometrically slower while they are not, and as slow as
/// permitted while a bucket is cold-stopped. The cost of backing off is bounded and visible --
/// at worst an entry is recorded <see cref="AuditLogSyncOptions.MaxInterval"/> late, and its
/// <c>occurred_at</c> still comes from VRChat, so nothing about the recorded history is less
/// accurate for having been learned later.
/// </para>
/// <para>
/// Every decision carries the reason that produced it. An adaptive schedule that only published
/// its interval would leave an operator unable to tell "quiet group" from "stuck producer" --
/// see <see cref="SyncDiagnostics"/>.
/// </para>
/// <para>
/// Jitter is applied on top for spec 4.2.2's reason: a fleet of Modbots that all decided on five
/// minutes would otherwise converge into a synchronised five-minute spike.
/// </para>
/// </remarks>
public sealed class AdaptivePollRate
{
    private readonly IModbotClock _clock;
    private readonly Random _random;

    private AuditLogSyncOptions _options;
    private int _quietPolls;

    public AdaptivePollRate(AuditLogSyncOptions options, IModbotClock clock, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options.Clamped();
        _clock = clock;

        // Injected so a test asserts on a schedule rather than on one lucky draw; production
        // passes the shared instance, which is what makes separate deployments disagree.
        _random = random ?? Random.Shared;

        Current = new PollRateDecision(
            _options.MinInterval,
            "starting up; polling at the fastest permitted rate until the group's rhythm is known",
            0,
            _clock.UtcNow);
    }

    /// <summary>The interval in force, and why.</summary>
    public PollRateDecision Current { get; private set; }

    /// <summary>The poll rate bounds in force. Changing them is spec 4.2.1's whole point.</summary>
    public AuditLogSyncOptions Options => _options;

    /// <summary>
    /// Adopts a poll rate the operator has changed, without waiting for the next poll to finish.
    /// </summary>
    /// <remarks>
    /// The interval in force is re-seated into the new bounds immediately rather than left to
    /// drift back over the following polls. An operator who has just widened the maximum because
    /// a sync is too chatty wants the next wait to be the long one; a poll rate that kept the old
    /// interval until the group happened to go quiet again would look like the setting had not
    /// taken.
    /// </remarks>
    public void Reconfigure(AuditLogSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var clamped = options.Clamped();
        if (clamped == _options)
            return;

        _options = clamped;

        var interval = Current.Interval;
        if (interval < clamped.MinInterval) interval = clamped.MinInterval;
        if (interval > clamped.MaxInterval) interval = clamped.MaxInterval;

        if (interval != Current.Interval)
        {
            Current = Current with
            {
                Interval = interval,
                Reason = $"{Current.Reason}; adjusted to the newly configured poll rate",
                DecidedAt = _clock.UtcNow,
            };
        }
    }

    /// <summary>
    /// Folds one pass's outcome into the poll rate and returns the new decision.
    /// </summary>
    public PollRateDecision Observe(AuditLogRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Current = Decide(result);
        return Current;
    }

    /// <summary>
    /// The current interval with per-tick jitter applied, which is what the loop actually waits.
    /// </summary>
    /// <remarks>
    /// <strong>Jitter only ever adds time.</strong> The interval's floor is spec 4.2's pacing cap,
    /// so a symmetric swing would spend half its draws under the cap and have to be clamped back
    /// to it -- which both breaks spec 4.2.1's "configuration may only make it gentler" and
    /// destroys the spread, since every negative draw would land on the same value.
    /// </remarks>
    public TimeSpan NextDelay()
    {
        var interval = Current.Interval;

        return _options.JitterFraction <= 0
            ? interval
            : interval + (interval * _random.NextDouble() * _options.JitterFraction);
    }

    private PollRateDecision Decide(AuditLogRunResult result)
    {
        var now = _clock.UtcNow;

        switch (result.Outcome)
        {
            case SyncOutcome.Produced:
                _quietPolls = 0;

                // Deliberately not "stay where we are": a burst that starts during a five-minute
                // interval should pull the poll rate all the way back immediately, because the
                // entries most worth having quickly are the ones that come in clusters.
                return new PollRateDecision(
                    _options.MinInterval,
                    result.CatchingUp
                        ? $"reading the group's existing audit log; {result.FactsWritten} new entries in the last page"
                        : $"{result.FactsWritten} new entries on the last poll; staying at the fastest permitted rate",
                    0,
                    now);

            case SyncOutcome.Quiet:
                _quietPolls++;
                var interval = Backoff(_quietPolls);

                return new PollRateDecision(
                    interval,
                    interval >= _options.MaxInterval
                        ? $"nothing new for {_quietPolls} polls; holding at the slowest rate until something happens"
                        : $"nothing new for {_quietPolls} polls; backing off",
                    _quietPolls,
                    now);

            case SyncOutcome.RateLimited:
                // Never a retry, and never a shorter wait than usual (spec 4.3.1). The gate will
                // refuse to send anything anyway; polling faster would only fill the log with
                // refusals and tell nobody anything the bucket health does not already say.
                return new PollRateDecision(
                    _options.MaxInterval,
                    "rate limited; waiting out the cold stop without probing",
                    _quietPolls,
                    now);

            case SyncOutcome.NotConfigured:
                return new PollRateDecision(
                    _options.MaxInterval,
                    "no managed group configured yet; idling until onboarding finishes",
                    _quietPolls,
                    now);

            case SyncOutcome.Failed:
            default:
                // Half the maximum: slow enough not to hammer whatever is broken, fast enough
                // that a transient failure does not cost five minutes of audit history.
                var retry = Halve(_options.MaxInterval);

                return new PollRateDecision(
                    retry,
                    $"last poll failed ({result.Message ?? "no detail"}); retrying at a reduced rate",
                    _quietPolls,
                    now);
        }
    }

    private TimeSpan Backoff(int quietPolls)
    {
        var interval = _options.MinInterval;

        for (var i = 1; i < quietPolls && interval < _options.MaxInterval; i++)
            interval *= _options.QuietBackoff;

        return interval > _options.MaxInterval ? _options.MaxInterval : interval;
    }

    private TimeSpan Halve(TimeSpan interval)
    {
        var halved = interval / 2;
        return halved < _options.MinInterval ? _options.MinInterval : halved;
    }
}
