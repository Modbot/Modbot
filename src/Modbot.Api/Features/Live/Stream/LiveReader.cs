using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Live.Stream;

/// <param name="Cursor">The id reading got to: the last fact read, wanted or not.</param>
/// <param name="More">Reading stopped with events still to look at.</param>
public sealed record LivePage(IReadOnlyList<LiveEvent> Events, long Cursor, bool More);

/// <summary>
/// Reads new facts after a cursor and turns the ones the live stream carries into
/// <see cref="LiveEvent"/>s (live updates design §2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same reader as the event WebSocket.</strong> <see cref="FactFeed"/>, with its rule
/// for ids that commit out of order, so a client resuming from a cursor misses nothing the
/// general stream would have sent it.
/// </para>
/// <para>
/// <strong>One lookup per page, not per event.</strong> A person's standing -- prior actions,
/// membership, a name when the fact carried none -- is what the roster and the Live page show,
/// and it is read for every person in a page at once, the way the roster reads it.
/// </para>
/// </remarks>
public sealed class LiveReader
{
    private readonly ModbotContext _db;
    private readonly FactFeed _feed;

    public LiveReader(ModbotContext db, TimeSpan? gapWait = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
        _feed = new FactFeed(db, gapWait);
    }

    /// <summary>The newest fact id, or 0 for an empty log. "From now" starts after it.</summary>
    public Task<long> NewestIdAsync(CancellationToken ct) => _feed.NewestIdAsync(ct);

    /// <summary>The oldest fact retention has kept, or null for an empty log.</summary>
    public Task<long?> OldestIdAsync(CancellationToken ct) => _feed.OldestIdAsync(ct);

    /// <summary>
    /// Up to <paramref name="limit"/> events after <paramref name="after"/> that this scope may
    /// be sent, reading past the ones it may not for at most <paramref name="maxPages"/> pages.
    /// </summary>
    /// <remarks>
    /// The cursor stops just before the first wanted event that did not fit, so the next read
    /// starts with it; everything before that was sent or is not for this caller.
    /// </remarks>
    public async Task<LivePage> ReadAsync(
        long after,
        int limit,
        LiveScope scope,
        DateTimeOffset now,
        int pageSize,
        int maxPages,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var events = new List<LiveEvent>();
        var cursor = after;

        for (var pages = 0; pages < maxPages; pages++)
        {
            var page = await _feed.ReadAsync(cursor, pageSize, now, ct);
            var built = await BuildAsync(page.Facts, scope, ct);

            foreach (var fact in page.Facts)
            {
                if (built.TryGetValue(fact.Id, out var @event) && scope.Wants(@event))
                {
                    if (events.Count == limit)
                        return new LivePage(events, cursor, More: true);

                    events.Add(@event);
                }

                cursor = fact.Id;
            }

            // Caught up, or stopped at a gap that is still being waited on.
            if (page.Facts.Count < pageSize)
                return new LivePage(events, cursor, More: false);

            if (events.Count == limit)
                return new LivePage(events, cursor, More: true);
        }

        return new LivePage(events, cursor, More: true);
    }

    /// <summary>
    /// Every fact as a live event, by fact id: the named kinds where a fact has one, and
    /// <see cref="LiveKinds.Fact"/> for the rest. Only the presence kinds describe a person, since
    /// that is what costs a lookup; every other page redraws by reading again.
    /// </summary>
    private async Task<Dictionary<long, LiveEvent>> BuildAsync(
        IReadOnlyList<ModbotEvent> facts,
        LiveScope scope,
        CancellationToken ct)
    {
        var result = new Dictionary<long, LiveEvent>();

        if (facts.Count == 0)
            return result;

        // A device is sent presence and nothing else, so nothing else is built for it.
        var live = facts
            .Select(f => (Fact: f, Kind: LiveKinds.Of(f.Type) ?? LiveKinds.Fact))
            .Where(x => !scope.IsDevice || LiveKinds.IsPresence(x.Kind))
            .ToList();

        if (live.Count == 0)
            return result;

        var people = live
            .Where(x => LiveKinds.IsPresence(x.Kind) && x.Fact.SubjectPlatform == FactPlatform.VRChat)
            .Select(x => x.Fact.SubjectId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var priorActions = people.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await ContextHandler.CountPriorActionsAsync(_db, people, ct);

        var members = people.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : await ContextHandler.CurrentMembersAsync(_db, people, ct);

        var ranks = people.Count == 0
            ? new Dictionary<string, TrustRank?>(StringComparer.Ordinal)
            : await ContextHandler.TrustRanksAsync(_db, people, ct);

        var payloads = live.ToDictionary(x => x.Fact.Id, x => AuditJson.Parse(x.Fact.Data));

        // Anybody the facts carried no name for, named from the stored profiles in one lookup,
        // as the Live page does.
        var nameless = live
            .Where(x => LiveKinds.IsPresence(x.Kind) && AuditJson.Text(payloads[x.Fact.Id], "displayName") is null)
            .Select(x => x.Fact.SubjectId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var names = nameless.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _db.VRChatUsers.AsNoTracking()
                .Where(u => nameless.Contains(u.UserId) && u.DisplayName != null)
                .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);

        // The world's name as stored now, so a client can say "The Black Cat #39047" rather than
        // print an id. One lookup for the page; a world nobody has read yet stays unnamed.
        var worldIds = live
            .Where(x => x.Fact.WorldId is not null)
            .Select(x => x.Fact.WorldId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var worldNames = worldIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _db.VRChatWorlds.AsNoTracking()
                .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
                .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);

        var device = scope.DeviceId?.ToString();

        foreach (var (fact, kind) in live)
        {
            var data = payloads[fact.Id];
            var id = fact.Id.ToString(CultureInfo.InvariantCulture);

            LivePerson? person = null;
            var flagged = false;
            string? reason = null;
            var finalKind = kind;

            if (LiveKinds.IsPresence(kind))
            {
                var name = AuditJson.Text(data, "displayName") ?? names.GetValueOrDefault(fact.SubjectId);
                var described = ContextHandler.Describe(fact.SubjectId, name, priorActions, members);

                person = new LivePerson(
                    fact.SubjectId,
                    name,
                    ranks.GetValueOrDefault(fact.SubjectId)?.ToString(),
                    described.Standing,
                    described.PriorActions,
                    described.Flags);

                if (kind == LiveKinds.PersonJoined && described.PriorActions > 0)
                {
                    finalKind = LiveKinds.FlaggedJoin;
                    flagged = true;
                    reason = described.PriorActions == 1
                        ? "1 prior moderation action"
                        : $"{described.PriorActions} prior moderation actions";
                }
            }

            var byThisDevice = device is not null
                && string.Equals(AuditJson.Text(data, "deviceId"), device, StringComparison.OrdinalIgnoreCase);

            result[fact.Id] = new LiveEvent(
                id,
                id,
                finalKind,
                fact.Type,
                fact.TypeRaw,
                AuditVisibility.CategoryOf(fact.Type) == AuditCategory.Moderation ? "moderation" : "operational",
                FactLabels.For(fact.Type),
                fact.Source.ToString(),
                fact.OccurredAt,
                fact.OccurredBefore,
                fact.ObservedAt,
                new LiveSubject(fact.SubjectPlatform.ToString(), fact.SubjectId, FactSubjects.For(fact.Type).ToString()),
                fact.ActorPlatform is { } platform && fact.ActorId is { } actorId
                    ? new LiveActor(platform.ToString(), actorId, AuditJson.Text(data, "actorDisplayName"))
                    : null,
                fact.InstanceId,
                fact.WorldId,
                fact.WorldId is { } worldId ? worldNames.GetValueOrDefault(worldId) : null,
                person,
                flagged,
                reason,
                byThisDevice,
                scope.IsDevice ? null : data);
        }

        return result;
    }
}
