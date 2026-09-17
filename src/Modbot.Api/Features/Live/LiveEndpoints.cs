using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion.Context;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Live;

/// <summary>A moderator whose client is in the instance right now.</summary>
public sealed record LiveWatcherView(string UserId, string? DisplayName, DateTimeOffset Since);

/// <summary>
/// Somebody in an instance. Exactly one of <paramref name="ArrivedAt"/> and <paramref name="HereBefore"/>
/// is set.
/// </summary>
/// <param name="ArrivedAt">When a moderator saw them walk in.</param>
/// <param name="HereBefore">
/// When they were first seen already there. They arrived at some earlier time nobody saw, and no
/// earlier time is made up for them.
/// </param>
/// <param name="TrustRank">Their VRChat trust rank as stored. Null when the profile's tags are not known yet.</param>
public sealed record LivePersonView(
    string UserId,
    string? DisplayName,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? HereBefore,
    string Standing,
    int PriorActions,
    IReadOnlyList<string> Flags,
    TrustRank? TrustRank = null);

/// <param name="HeadCount">How many people are in the instance, whether or not anybody is watching it.</param>
/// <param name="People">Everyone present now. Empty whenever nobody is watching.</param>
/// <param name="LastWatchedAt">When the last moderator stopped watching. Null while somebody is.</param>
/// <param name="LastSeen">Who was there at <paramref name="LastWatchedAt"/>. Not "here now".</param>
public sealed record LiveInstanceView(
    Guid Id,
    string WorldId,
    string? WorldName,
    string? WorldImageUrl,
    string? VRChatInstanceId,
    string? GroupAccessType,
    string? Region,
    DateTimeOffset OpenedAt,
    int? HeadCount,
    IReadOnlyList<LiveWatcherView> Watching,
    IReadOnlyList<LivePersonView> People,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<LivePersonView> LastSeen);

public sealed record LiveView(IReadOnlyList<LiveInstanceView> Instances, DateTimeOffset GeneratedAt);

/// <summary>
/// The Live page: the group's open instances right now, and who is in each.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every open group instance is listed, watched or not.</strong> The instances come from the
/// group's instance list and the head count from each instance's own page, both read by syncs with no
/// client involved. Only the people need a moderator watching, because VRChat's instance API does
/// not say who is inside. An instance nobody is watching shows its world, number and head count, and
/// who was last seen there if anybody watched it earlier.
/// </para>
/// <para>
/// <strong>Nothing here calls VRChat.</strong> Every field is from Modbot's own tables, so a page
/// that asks every five seconds spends none of the group's API budget (foundation 4.3.4).
/// </para>
/// <para>
/// <strong>Who is present</strong> is <see cref="InstanceWatching"/>'s rule, the same one the overlay
/// roster and the Discord card use.
/// </para>
/// <para>
/// <strong>Every parameter is explicitly attributed.</strong> An unattributed concrete type on a
/// GET is bound as a body, which throws while routes are mapped and takes every endpoint with it.
/// </para>
/// </remarks>
public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLive(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/live").WithTags("Live").RequireAuthorization()
            .MapGet("/", async (
                    [FromServices] ModbotContext db,
                    [FromServices] IModbotClock clock,
                    CancellationToken ct) =>
                Results.Ok(await ReadAsync(db, clock.UtcNow, ct)))
            .RequiresFlag(ModbotPermissions.ViewLiveInstances)
            .WithName("GetLiveInstances")
            .WithSummary("The group's open instances right now, and who is in each")
            .WithDescription(
                "Read from Modbot's own tables; nothing here calls VRChat. Every open group "
                + "instance is listed with its head count whether or not a moderator is in it. "
                + "`people` is filled only while a moderator's client is watching; otherwise "
                + "`lastSeen` holds who was there when watching last stopped, at `lastWatchedAt`.")
            .Produces<LiveView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    internal static async Task<LiveView> ReadAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Read, never created: a GET that writes the settings row is a surprise.
        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { Length: > 0 })
            return new LiveView([], now);

        var instances = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null)
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(ct);

        if (instances.Count == 0)
            return new LiveView([], now);

        var worldIds = instances.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var worlds = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.ImageUrl, w.ThumbnailImageUrl })
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        var people = await new InstancePeopleReader(db).ForInstancesAsync(instances, ct);

        var everyone = people.Values
            .SelectMany(p => p.Here.Concat(p.LastSeen))
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var priorActions = await ContextHandler.CountPriorActionsAsync(db, everyone, ct);
        var members = await ContextHandler.CurrentMembersAsync(db, everyone, ct);

        // The stored profiles, in one lookup: a name for anybody the facts carried none for, and
        // the trust rank for everybody, which lives nowhere but the profile row.
        var anybody = people.Values
            .SelectMany(p => p.Here.Concat(p.LastSeen).Select(x => x.UserId)
                .Concat(p.Watching.Select(w => w.UserId)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var profiles = anybody.Count == 0
            ? new Dictionary<string, (string? Name, TrustRank? Rank)>(StringComparer.Ordinal)
            : await db.VRChatUsers.AsNoTracking()
                .Where(u => anybody.Contains(u.UserId))
                .Select(u => new { u.UserId, u.DisplayName, u.TrustRank })
                .ToDictionaryAsync(u => u.UserId, u => (Name: u.DisplayName, Rank: u.TrustRank), StringComparer.Ordinal, ct);

        var names = profiles
            .Where(p => p.Value.Name is not null)
            .ToDictionary(p => p.Key, p => p.Value.Name!, StringComparer.Ordinal);

        LivePersonView Person(PersonHere p)
        {
            var name = p.DisplayName ?? names.GetValueOrDefault(p.UserId);
            var described = ContextHandler.Describe(p.UserId, name, priorActions, members);

            return new LivePersonView(
                p.UserId,
                name,
                p.SeenArriving ? p.Since : null,
                p.SeenArriving ? null : p.Since,
                described.Standing,
                described.PriorActions,
                described.Flags,
                profiles.GetValueOrDefault(p.UserId).Rank);
        }

        var views = instances.Select(instance =>
        {
            var world = worlds.GetValueOrDefault(instance.WorldId);
            var inInstance = people[instance.Id];

            return new LiveInstanceView(
                instance.Id,
                instance.WorldId,
                world?.Name,
                world?.ThumbnailImageUrl ?? world?.ImageUrl,
                instance.VRChatInstanceId,
                instance.GroupAccessType,
                instance.Region,
                instance.OpenedAt,
                HeadCounts.Shown(instance),
                inInstance.Watching
                    .Select(w => new LiveWatcherView(w.UserId, w.DisplayName ?? names.GetValueOrDefault(w.UserId), w.Since))
                    .ToList(),
                inInstance.Here.Select(Person).ToList(),
                inInstance.LastWatchedAt,
                inInstance.LastSeen.Select(Person).ToList());
        }).ToList();

        return new LiveView(views, now);
    }
}
