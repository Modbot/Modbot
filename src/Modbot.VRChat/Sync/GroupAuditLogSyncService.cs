using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Runs the audit-log producer on an adaptive pollRate.
/// </summary>
/// <remarks>
/// <para>
/// Its own hosted service, separate from group info, so that a cold stop on one endpoint class
/// cannot stop the other. Spec 4.3.1 is explicit that a 429 halts the most specific bucket that
/// matched and nothing else, and 4.2.3 singles this producer out as the one to protect when the
/// ceiling binds — a design where both producers shared a loop would quietly throw that away.
/// </para>
/// <para>
/// The loop never retries a failed pass immediately. Everything it could do about a failure, the
/// poll rate has already decided (see <see cref="AdaptivePollRate"/>), and a retry inside the loop
/// would be exactly the premature probe spec 4.3.4 attributes 45–80 seconds of self-inflicted
/// penalty to.
/// </para>
/// </remarks>
public sealed class GroupAuditLogSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly AdaptivePollRate _pollRate;
    private readonly ISyncPacingSource? _pacing;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    public GroupAuditLogSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        AuditLogSyncOptions? options = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _scopes = scopes;
        _clock = clock;
        _diagnostics = diagnostics;
        _pollRate = new AdaptivePollRate(options ?? new AuditLogSyncOptions(), clock);
        _pacing = pacing;
        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The poll rate decision in force, for tests and for the health screen.</summary>
    internal AdaptivePollRate PollRate => _pollRate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits, rather than firing the instant the process is up. Spec 4.2.2's
        // whole argument is that a fleet restarting together must re-spread instead of marching
        // in lockstep, and a platform redeploy restarts every server at once.
        _diagnostics.RecordPollRate(_pollRate.Current);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-read before the wait is computed, so a rate the operator lowered a second ago
            // applies to the very next poll rather than to the one after it (spec 4.2.1). One
            // single-row read per tick, at most once per eight seconds.
            await RefreshPacingAsync(stoppingToken).ConfigureAwait(false);

            if (!await WaitAsync(_pollRate.NextDelay(), stoppingToken).ConfigureAwait(false))
                return;

            var result = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            var previous = _pollRate.Current;
            var decision = _pollRate.Observe(result);

            _diagnostics.RecordPollRate(decision);

            if (decision.Interval != previous.Interval)
            {
                // Logged on change only. An operator looking at a producer that has decided to
                // poll every five minutes needs to be able to find out that it decided, and why,
                // without a line every eight seconds burying it.
                _log.Debug(
                    "Audit-log poll rate is now {Interval}: {Reason}",
                    decision.Interval,
                    decision.Reason);
            }
        }
    }

    /// <summary>
    /// Adopts a poll rate the operator has changed. Failures are the provider's to swallow.
    /// </summary>
    /// <remarks>
    /// The scoped <see cref="GroupAuditLogSync"/> this loop creates per run reads the same
    /// snapshot from the container, so the interval this tick waits and the page size the pass
    /// uses always came from one read.
    /// </remarks>
    private async Task RefreshPacingAsync(CancellationToken ct)
    {
        if (_pacing is null)
            return;

        try
        {
            var pacing = await _pacing.CurrentAsync(ct).ConfigureAwait(false);
            _pollRate.Reconfigure(pacing.AuditLog);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<AuditLogRunResult> RunOnceAsync(CancellationToken ct)
    {
        var started = _clock.UtcNow;

        try
        {
            using var scope = _scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<GroupAuditLogSync>();

            var result = await sync.RunOnceAsync(ct).ConfigureAwait(false);
            Report(result, started, Describe(result));

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AuditLogRunResult(SyncOutcome.Quiet, Message: "shutting down");
        }
        catch (Exception e)
        {
            // The audit log is the authoritative moderation source, so a producer that dies on one
            // bad page and never comes back is a far worse outcome than one that logs and tries
            // again later. Nothing is lost by continuing: the cursor did not move.
            _log.Error(e, "Audit-log sync failed. The cursor was not advanced; the next pass re-reads");

            var result = new AuditLogRunResult(SyncOutcome.Failed, Message: e.Message);
            Report(result, started, e.Message);

            return result;
        }
    }

    private void Report(AuditLogRunResult result, DateTimeOffset started, string summary) =>
        _diagnostics.RecordAuditLogRun(
            new SyncRunReport(result.Outcome, started, _clock.UtcNow - started, summary));

    private static string Describe(AuditLogRunResult result) => result.Outcome switch
    {
        SyncOutcome.Produced =>
            $"{result.FactsWritten} new, {result.AlreadyRecorded} already recorded"
            + (result.CatchingUp ? "; reading existing history" : string.Empty),

        SyncOutcome.Quiet =>
            "nothing new",

        _ => result.Message ?? result.Outcome.ToString(),
    };

    /// <summary>Waits, reporting whether the wait completed rather than throwing on shutdown.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _delays.DelayAsync(delay, ct).ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
