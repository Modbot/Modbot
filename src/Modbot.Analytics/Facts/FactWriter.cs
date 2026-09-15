using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Analytics.Facts;

/// <summary>
/// Appends facts to <c>modbot_event</c>, deduplicating client reports at the ingest boundary.
/// </summary>
/// <remarks>
/// <para>
/// Deduplication is a <em>windowed range check</em>, not a unique index (spec 5.7.1). Ranges are
/// not expressible as a unique constraint, and the bucketed alternative -- hashing
/// <c>floor(timestamp / 5s)</c> -- fails at bucket boundaries: two clients reporting the same
/// join at 14:00:04.9 and 14:00:05.1 land in different buckets and both get written. That failure
/// is silent and intermittent, which is the worst kind.
/// </para>
/// <para>
/// A range check alone is not enough either, because it is a check-then-insert: six clients
/// reporting at once all read "no existing fact" before any of them writes. Serialisation comes
/// from a transaction-scoped PostgreSQL advisory lock keyed on the logical event, so only reports
/// of the <em>same</em> event queue behind one another and unrelated ingest runs at full speed.
/// </para>
/// </remarks>
public sealed class FactWriter : IFactWriter
{
    /// <summary>
    /// Used until the settings row exists. Spec 5.7.1: bounded above by the 15-second genuine
    /// leave-and-rejoin, below by residual clock skew after clients synchronise to server time.
    /// </summary>
    public const int DefaultWindowSeconds = 5;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    /// <summary>
    /// Joins the key parts. A control character rather than ':' because VRChat ids are arbitrary
    /// user-chosen text (spec 3.1.1) and a printable separator could appear inside one, letting
    /// two different events collide on one lock.
    /// </summary>
    private const char KeySeparator = '\u001f';

    private int? _windowSeconds;

    private readonly FactSignal? _signal;

    /// <param name="signal">Pulsed after each insert, so live readers look now rather than at their next check.</param>
    public FactWriter(ModbotContext db, IModbotClock clock, FactSignal? signal = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _signal = signal;
    }

    public async Task<FactWriteResult> WriteAsync(FactRecord fact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        // The one place a fact type is checked. It is checked for shape, never against a list:
        // a type Modbot has never seen is legitimate and must be storable, but "Banned",
        // "vrchat..ban" and "VRChat.Ban" are bugs in a producer, and letting them into a log that
        // is queried by prefix for years would cost far more than refusing them here.
        if (!FactType.IsWellFormed(fact.Type))
        {
            throw new ArgumentException(
                $"'{fact.Type}' is not a well-formed fact type. Expected lowercase dot-separated "
                + "segments such as 'vrchat.group.member.ban'; see FactType.",
                nameof(fact));
        }

        ArgumentNullException.ThrowIfNull(fact);

        if (!FactDeduplication.AppliesTo(fact.Source))
            return new FactWriteResult(await InsertAsync(fact, ct), WasDeduplicated: false);

        var window = TimeSpan.FromSeconds(await WindowSecondsAsync(ct));

        // Transaction-scoped, so the lock is released by the commit that makes the new fact
        // visible -- the next holder therefore always sees it.
        // The instant, not the clock reading: the client sends VRChat's local time, and the
        // window arithmetic below and the row both want UTC. The context converts on the way to
        // the database as well; this keeps the value the caller gets back consistent with it.
        fact = fact with { OccurredAt = fact.OccurredAt.ToUniversalTime() };

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            var key = LockKey(fact);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({key})::bigint)", ct);

            var existing = await FindWithinWindowAsync(fact, window, ct);
            if (existing is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);

                return new FactWriteResult(existing.Value, WasDeduplicated: true);
            }

            var id = await InsertAsync(fact, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new FactWriteResult(id, WasDeduplicated: false);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<FactWriteResult>> WriteManyAsync(
        IEnumerable<FactRecord> facts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var results = new List<FactWriteResult>();

        // One at a time: each deduplicated fact takes its own lock, and holding several at once
        // across a batch is how you turn independent ingest into a deadlock.
        foreach (var fact in facts)
            results.Add(await WriteAsync(fact, ct));

        return results;
    }

    /// <summary>
    /// Identifies the logical event: the same person doing the same thing in the same instance.
    /// </summary>
    /// <remarks>
    /// The timestamp is deliberately absent. Including it would put reports of one event under
    /// different locks, which is exactly the serialisation the lock exists to provide.
    /// <see cref="ModbotEvent.WorldId"/> is part of the key because an instance id identifies a
    /// session only within its world.
    /// </remarks>
    private static string LockKey(FactRecord fact) => string.Join(
        KeySeparator,
        (short)fact.SubjectPlatform,
        fact.SubjectId,
        fact.Type,
        fact.WorldId ?? string.Empty,
        fact.InstanceId ?? string.Empty);

    private async Task<long?> FindWithinWindowAsync(
        FactRecord fact,
        TimeSpan window,
        CancellationToken ct)
    {
        var from = fact.OccurredAt - window;
        var to = fact.OccurredAt + window;

        var match = await _db.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == fact.SubjectPlatform
                     && e.SubjectId == fact.SubjectId
                     && e.Type == fact.Type
                     && e.WorldId == fact.WorldId
                     && e.InstanceId == fact.InstanceId
                     && e.OccurredAt >= from
                     && e.OccurredAt <= to)
            // Earliest wins: spec 5.7 says whichever report arrives first sets the window, and
            // ordering by time rather than id keeps that stable if ids are ever filled in later.
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => (long?)e.Id)
            .FirstOrDefaultAsync(ct);

        return match;
    }

    private async Task<long> InsertAsync(FactRecord fact, CancellationToken ct)
    {
        var data = await WithHeldRolesAsync(fact, ct);

        var entity = new ModbotEvent
        {
            OccurredAt = fact.OccurredAt,
            OccurredBefore = fact.OccurredBefore,
            ObservedAt = _clock.UtcNow,
            Type = fact.Type,
            TypeRaw = fact.TypeRaw,
            SubjectPlatform = fact.SubjectPlatform,
            SubjectId = fact.SubjectId,
            ActorPlatform = fact.ActorPlatform,
            ActorId = fact.ActorId,
            WorldId = fact.WorldId,
            InstanceId = fact.InstanceId,
            Source = fact.Source,
            Data = data?.ToJsonString() ?? "{}",
        };

        _db.Events.Add(entity);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // A failed insert -- most often no partition covers occurred_at -- must not leave the
            // fact queued to be retried by the next unrelated SaveChanges on this context.
            _db.Entry(entity).State = EntityState.Detached;
            throw;
        }

        _db.Entry(entity).State = EntityState.Detached;
        _signal?.Pulse();

        return entity.Id;
    }

    /// <summary>
    /// The payload with the roles its people held when it happened saved alongside (see
    /// <see cref="HeldRoles"/>), so a Discord route decides on the roles of the moment rather than
    /// the roles when it posts.
    /// </summary>
    /// <remarks>
    /// Skipped for client presence reports: they are the busiest ingest there is, and a route that
    /// meets one works the roles out from the recorded role changes instead. Skipped too when the
    /// producer already supplied roles, and when nobody in the fact is a person.
    /// </remarks>
    private async Task<System.Text.Json.Nodes.JsonObject?> WithHeldRolesAsync(FactRecord fact, CancellationToken ct)
    {
        if (fact.Source == FactSource.Client || fact.Data?.ContainsKey(HeldRoles.Key) == true)
            return fact.Data;

        var people = await PeopleDirectory.LoadAsync(
            _db, [(fact.SubjectPlatform, fact.SubjectId), (fact.ActorPlatform, fact.ActorId)], ct);

        _groupId ??= await _db.Settings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var roles = await RoleHistory.ForFactAsync(
            _db, people, _groupId, fact.SubjectPlatform, fact.SubjectId, fact.ActorPlatform, fact.ActorId, fact.OccurredAt, ct);

        if (roles is null)
            return fact.Data;

        // A copy: the caller's object may be reused for the next fact.
        var data = fact.Data is null
            ? new System.Text.Json.Nodes.JsonObject()
            : (System.Text.Json.Nodes.JsonObject)fact.Data.DeepClone();

        data[HeldRoles.Key] = roles.ToJson();
        return data;
    }

    private string? _groupId;

    private async Task<int> WindowSecondsAsync(CancellationToken ct)
    {
        // Read, never created: ingest can legitimately run before onboarding writes the row, and
        // a read path that writes is a surprise nobody wants in the hot loop.
        _windowSeconds ??= await _db.Settings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.DedupWindowSeconds)
            .FirstOrDefaultAsync(ct) ?? DefaultWindowSeconds;

        return _windowSeconds.Value;
    }
}
