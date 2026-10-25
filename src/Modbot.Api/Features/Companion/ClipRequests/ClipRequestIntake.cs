using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.Evidence.Options;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Companion.ClipRequests;

/// <summary>
/// Reads new action facts and writes a <see cref="ClipRequest"/> for each one the group asks a
/// clip for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is responsible.</strong> An action made in Modbot is a <c>modbot.action.*</c> fact
/// whose actor is the moderator. VRChat's own record of that action names Modbot's service
/// account, which says nothing about a person, so it is skipped. An action made in VRChat's menu
/// is a group audit fact whose actor is a VRChat id; it counts when that id belongs to a Modbot
/// account. Anything else (AutoMod, an unlinked account) has nobody to ask.
/// </para>
/// <para>
/// <strong>One decision, one request.</strong> A ban made in VRChat also writes an instance kick
/// from the same request (<see cref="LinkedActions.Window"/>). That kick is a follower of the ban
/// and is skipped when bans are asked for; the ban's request takes the instance from it. With bans
/// not ticked the ban asks nothing, so the kick is asked on its own.
/// </para>
/// <para>
/// <strong>Where the person was.</strong> The instance kick names it. Without one, the person's
/// presence facts say. If neither does, nobody can be said to have been there with them.
/// </para>
/// <para>
/// The cursor is a fact id in <see cref="ClipRequestCursor"/>. It starts at the newest fact, so
/// ticking a setting on never asks about the past, and it moves past facts the group does not ask a
/// clip for.
/// </para>
/// </remarks>
public sealed class ClipRequestIntake
{
    private const int PageSize = 200;
    private const int MaxPagesPerPass = 10;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DeviceLocations _locations;
    private readonly ClipRequestStore _store;

    public ClipRequestIntake(ModbotContext db, IModbotClock clock, DeviceLocations locations, ClipRequestStore store)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(store);

        _db = db;
        _clock = clock;
        _locations = locations;
        _store = store;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        await _store.ExpireAsync(now, ct);

        var feed = new FactFeed(_db);
        var cursor = await _db.ClipRequestCursors.FirstOrDefaultAsync(ct);

        if (cursor is null)
        {
            _db.ClipRequestCursors.Add(new ClipRequestCursor { ReadThrough = await feed.NewestIdAsync(ct) });
            await _db.SaveChangesAsync(ct);
            return;
        }

        var settings = await _db.GetSettingsAsync(ct);

        // No evidence storage, no clip: there is nowhere to put one.
        var askingAnything = (settings.ClipOnBans || settings.ClipOnKicks || settings.ClipOnWarnings)
            && EvidenceSettingsBinder.ToBackend(settings.EvidenceBackend) is not EvidenceBackend.None;

        for (var pass = 0; pass < MaxPagesPerPass; pass++)
        {
            var page = await feed.ReadAsync(cursor.ReadThrough, PageSize, now, ct);
            if (page.Facts.Count == 0)
                return;

            if (askingAnything)
            {
                foreach (var fact in page.Facts)
                    await ConsiderAsync(fact, settings, now, ct);
            }

            cursor.ReadThrough = page.Through;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task ConsiderAsync(ModbotEvent fact, Core.Data.Entities.Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (ClipRequestRule.ActionOf(fact.Type) is not { } action
            || !ClipRequestRule.IsSwitchedOn(action, settings.ClipOnBans, settings.ClipOnKicks, settings.ClipOnWarnings)
            || !ClipRequestRule.IsRecentEnough(fact.OccurredAt, now))
            return;

        if (await ModeratorOfAsync(fact, settings, ct) is not { } moderator)
            return;

        // The instance kick that comes with a ban or a removal is the same decision.
        if (fact.Type == FactType.GroupInstanceKick && await HasMainFactAsync(fact, settings.ClipOnBans, ct))
            return;

        if (await _db.ClipRequests.AnyAsync(r => r.FactId == fact.Id, ct))
            return;

        var (worldId, instanceId) = await WhereAsync(fact, ct);

        Guid? device = null;
        if (instanceId is not null)
        {
            var devices = await _db.CompanionDevices.AsNoTracking()
                .Where(d => d.IssuedToUserId == moderator && d.RevokedAt == null)
                .OrderByDescending(d => d.LastSeenAt)
                .Select(d => d.Id)
                .ToListAsync(ct);

            device = ClipRequestRule.DeviceToAsk(devices, id => _locations.IsIn(id, instanceId, now, worldId));
        }

        _db.ClipRequests.Add(new ClipRequest
        {
            FactId = fact.Id,
            Action = action,
            SubjectId = fact.SubjectId,
            ModeratorUserId = moderator,
            ActionAt = fact.OccurredAt,
            WorldId = worldId,
            InstanceId = instanceId,
            Status = device is null ? ClipStatus.NotInInstance : ClipStatus.Asked,
            CreatedAt = now,
            DeviceId = device,
            RequestedAt = device is null ? null : now,
        });
    }

    private async Task<Guid?> ModeratorOfAsync(ModbotEvent fact, Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        if (fact.Type is FactType.ActionBan or FactType.ActionKick)
        {
            return fact.ActorPlatform == FactPlatform.Modbot && Guid.TryParse(fact.ActorId, out var userId)
                ? userId
                : null;
        }

        if (fact.ActorPlatform != FactPlatform.VRChat
            || fact.ActorId is not { Length: > 0 } actor
            || string.Equals(actor, settings.VRChatAccountUserId, StringComparison.Ordinal))
            return null;

        return await _db.Users.AsNoTracking()
            .Where(u => u.VRChatUserId == actor)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    private Task<bool> HasMainFactAsync(ModbotEvent kick, bool bansAreAsked, CancellationToken ct)
    {
        var from = kick.OccurredAt - LinkedActions.Window;
        var to = kick.OccurredAt + LinkedActions.Window;

        // The kick is only a follower when the main action will be asked for itself. This kick's own
        // switch is on (ConsiderAsync checked), so a removal counts; a ban counts only if bans are asked.
        return _db.Events.AsNoTracking().AnyAsync(e =>
            ((e.Type == FactType.MemberBanned && bansAreAsked) || e.Type == FactType.MemberKicked)
            && e.SubjectId == kick.SubjectId
            && e.OccurredAt >= from
            && e.OccurredAt <= to, ct);
    }

    private async Task<(string? WorldId, string? InstanceId)> WhereAsync(ModbotEvent fact, CancellationToken ct)
    {
        if (fact.InstanceId is { Length: > 0 })
            return (fact.WorldId, fact.InstanceId);

        var from = fact.OccurredAt - LinkedActions.Window;
        var to = fact.OccurredAt + LinkedActions.Window;

        var kick = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.GroupInstanceKick
                && e.SubjectId == fact.SubjectId
                && e.InstanceId != null
                && e.OccurredAt >= from
                && e.OccurredAt <= to)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.WorldId, e.InstanceId })
            .FirstOrDefaultAsync(ct);

        if (kick is not null)
            return (kick.WorldId, kick.InstanceId);

        var types = ClipsNearAPerson.PresenceTypes.ToList();

        var marks = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && e.SubjectId == fact.SubjectId
                && types.Contains(e.Type)
                && e.InstanceId != null
                && e.OccurredAt >= fact.OccurredAt - TimeInInstance.LongestStay
                && e.OccurredAt <= fact.OccurredAt)
            .Select(e => new PersonMark(e.Type, e.WorldId, e.InstanceId!, e.OccurredAt))
            .ToListAsync(ct);

        return ClipRequestRule.WhereWas(marks, fact.OccurredAt) is { } there
            ? (there.WorldId, there.InstanceId)
            : (null, null);
    }
}

/// <summary>Runs <see cref="ClipRequestIntake"/> every few seconds, one scope per pass.</summary>
public sealed class ClipRequestWatcher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;

    public ClipRequestWatcher(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ClipRequestIntake>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "A clip request pass failed; trying again shortly");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
