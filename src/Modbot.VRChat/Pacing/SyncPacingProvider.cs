using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Pacing;

/// <summary>
/// The pacing in force, re-read while Modbot is running.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.1 says the rates are operator-configurable. The producers are long-lived
/// <c>BackgroundService</c>s and the limiter is a process-wide singleton, so "configurable" is
/// only true if a change reaches them without a restart — a setting that silently needed one
/// would be worse than a screen that admitted it could not be changed, which is what this
/// replaces.
/// </para>
/// <para>
/// <strong>Re-read per run, not per request.</strong> Each producer asks for the current pacing
/// once per tick, before it decides how long to wait; the limiter asks once per acquisition and
/// re-applies only when the answer has actually changed. A single-row read every eight seconds at
/// the very fastest is not a load worth caching around, and a snapshot taken per run means the
/// interval a poll waits and the page size it uses always came from the same read.
/// </para>
/// <para>
/// A short cache sits underneath anyway, because the limiter's acquisition path is hot and a
/// database round trip per outbound VRChat call would be a real cost for a value that changes
/// when somebody moves a slider. A write through the API pushes the new document in directly, so
/// the cache never delays an operator's own change — the interval only bounds how long a
/// hand-edited row takes to be noticed.
/// </para>
/// </remarks>
public interface ISyncPacingSource
{
    /// <summary>The last resolved pacing, without touching the database.</summary>
    /// <remarks>
    /// For callers that have just awaited <see cref="CurrentAsync"/> higher up the same run —
    /// the scoped sync objects, built by the container after their producer refreshed. It is
    /// spec 4.2's defaults until the first successful read.
    /// </remarks>
    SyncPacing Snapshot { get; }

    /// <summary>
    /// Increments whenever <see cref="Snapshot"/> becomes a different document.
    /// </summary>
    /// <remarks>
    /// So a consumer that has to do work on a change — the limiter, which reconfigures every
    /// bucket — can tell "unchanged" from "changed back to the same value" cheaply, on the
    /// request path, without comparing documents.
    /// </remarks>
    long Version { get; }

    /// <summary>Reads the pacing, from the cache when it is fresh enough.</summary>
    ValueTask<SyncPacing> CurrentAsync(CancellationToken ct = default);

    /// <summary>Publishes a document that has just been written, without a read to find it.</summary>
    void Publish(string? json);
}

/// <inheritdoc cref="ISyncPacingSource"/>
public sealed class SyncPacingProvider : ISyncPacingSource
{
    /// <summary>
    /// How long a cached document is served for.
    /// </summary>
    /// <remarks>
    /// Short enough that a row changed outside the API is picked up within a poll or two, long
    /// enough that a member sweep does not read the settings row once per page. It is a backstop:
    /// the supported path — the settings screen — publishes its change immediately.
    /// </remarks>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly SyncPacingBaseline? _baseline;
    private readonly TimeSpan _cacheFor;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _reading = new(1, 1);

    private SyncPacing _snapshot;
    private string? _json;
    private DateTimeOffset _readAt;
    private bool _read;
    private long _version;

    public SyncPacingProvider(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncPacingBaseline? baseline = null,
        TimeSpan? cacheFor = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
        _baseline = baseline;
        _cacheFor = cacheFor ?? CacheFor;
        _snapshot = SyncPacing.Resolve(SyncPacingDocument.Empty, classes: null, baseline);
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public SyncPacing Snapshot => Volatile.Read(ref _snapshot);

    public long Version => Interlocked.Read(ref _version);

    public async ValueTask<SyncPacing> CurrentAsync(CancellationToken ct = default)
    {
        if (IsFresh())
            return Snapshot;

        await _reading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh())
                return Snapshot;

            string? json = null;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

                json = await db.Settings
                    .AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => s.SyncPacing)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // A producer that stopped because the settings row was briefly unreadable would
                // be a far worse outcome than one that keeps the pacing it already had. The last
                // snapshot -- spec 4.2's defaults, until a read has succeeded -- is safe by
                // construction, because every rate in it is a cap rather than a target.
                _log.Debug(e, "Could not re-read sync pacing; keeping the pacing already in force");

                // Marked as read so the failure is not retried on every acquisition.
                _readAt = _clock.UtcNow;
                _read = true;

                return Snapshot;
            }

            Apply(json);

            return Snapshot;
        }
        finally
        {
            _reading.Release();
        }
    }

    public void Publish(string? json)
    {
        _reading.Wait();
        try
        {
            Apply(json);
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>Stores the document and bumps the version if it is not the one already held.</summary>
    private void Apply(string? json)
    {
        _readAt = _clock.UtcNow;
        _read = true;

        if (string.Equals(_json, json, StringComparison.Ordinal))
            return;

        _json = json;
        Volatile.Write(
            ref _snapshot,
            SyncPacing.Resolve(SyncPacingJson.Read(json), classes: null, _baseline));

        Interlocked.Increment(ref _version);
    }

    private bool IsFresh() => _read && _clock.UtcNow - _readAt < _cacheFor;
}
