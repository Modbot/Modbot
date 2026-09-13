using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.RateLimiting;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Runs the audit-log producer on an adaptive cadence.
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
/// cadence has already decided (see <see cref="AdaptiveCadence"/>), and a retry inside the loop
/// would be exactly the premature probe spec 4.3.4 attributes 45–80 seconds of self-inflicted
/// penalty to.
/// </para>
/// </remarks>
public sealed class GroupAuditLogSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly AdaptiveCadence _cadence;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    /// <summary>Null until the first successful check, so the first pass always verifies.</summary>
    private DateTimeOffset? _vocabularyCheckedAt;

    public GroupAuditLogSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        AuditLogSyncOptions? options = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _scopes = scopes;
        _clock = clock;
        _diagnostics = diagnostics;
        _cadence = new AdaptiveCadence(options ?? new AuditLogSyncOptions(), clock);
        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass waits, rather than firing the instant the process is up. Spec 4.2.2's
        // whole argument is that a fleet restarting together must re-spread instead of marching
        // in lockstep, and a platform redeploy restarts every instance at once.
        _diagnostics.RecordCadence(_cadence.Current);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitAsync(_cadence.NextDelay(), stoppingToken).ConfigureAwait(false))
                return;

            var result = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            var previous = _cadence.Current;
            var decision = _cadence.Observe(result);

            _diagnostics.RecordCadence(decision);

            if (decision.Interval != previous.Interval)
            {
                // Logged on change only. An operator looking at a producer that has decided to
                // poll every five minutes needs to be able to find out that it decided, and why,
                // without a line every eight seconds burying it.
                _log.Debug(
                    "Audit-log cadence is now {Interval}: {Reason}",
                    decision.Interval,
                    decision.Reason);
            }
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

            // After the sync, never before it: the vocabulary check is a diagnostic and must not
            // delay the moderation history by even one pass. It is also skipped entirely when the
            // deployment has no group yet, which is what NotConfigured means here.
            if (result.Outcome is not SyncOutcome.NotConfigured)
                await MaybeVerifyVocabularyAsync(scope, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Asks VRChat what its audit log can contain, and checks the mapping table against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GroupAuditLogEvents</c> is a table of observed strings — VRChat types <c>eventType</c>
    /// as a bare string, so there is no enum to compile against. Without this check the only way
    /// to discover a wrong spelling was to wait for a real event to arrive unmapped, which for a
    /// misspelled type never happens at all: Modbot waits for a name VRChat never emits, and the
    /// fact log looks healthy while missing every ban.
    /// </para>
    /// <para>
    /// Failures are swallowed. A diagnostic that cannot run is an inconvenience; the audit log
    /// stopping because a diagnostic failed would be a defect.
    /// </para>
    /// </remarks>
    private async Task MaybeVerifyVocabularyAsync(IServiceScope scope, CancellationToken ct)
    {
        if (_vocabularyCheckedAt is { } last
            && _clock.UtcNow - last < AuditLogVocabulary.RefreshInterval)
        {
            return;
        }

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(settings.ManagedGroupId)) return;

            var vocabulary = scope.ServiceProvider.GetRequiredService<AuditLogVocabulary>();
            var report = await vocabulary.CheckAsync(settings.ManagedGroupId, ct).ConfigureAwait(false);

            if (report is null) return;

            vocabulary.Report(report);
            _diagnostics.RecordVocabulary(report);
            _vocabularyCheckedAt = _clock.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception e)
        {
            _log.Debug(e, "Audit-log vocabulary check failed. Sync is unaffected");
        }
    }

    private void Report(AuditLogRunResult result, DateTimeOffset started, string summary) =>
        _diagnostics.RecordAuditLogRun(
            new SyncRunReport(result.Outcome, started, _clock.UtcNow - started, summary));

    private static string Describe(AuditLogRunResult result) => result.Outcome switch
    {
        SyncOutcome.Produced =>
            $"{result.FactsWritten} new, {result.AlreadyRecorded} already recorded, "
            + $"{result.EntriesRead} read over {result.PagesRead} request(s)"
            + (result.Backfilling ? "; still reading existing history" : string.Empty),

        SyncOutcome.Quiet =>
            $"nothing new; {result.EntriesRead} entries re-read over {result.PagesRead} request(s)",

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
