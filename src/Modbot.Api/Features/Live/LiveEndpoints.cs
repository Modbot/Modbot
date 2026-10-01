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

/// <param name="InstanceName">The name the instance was opened with, or null when it has none. Shown in place of the number.</param>
/// <param name="HeadCount">How many people are in the instance, whether or not anybody is watching it.</param>
/// <param name="People">Everyone present now. Empty whenever nobody is watching.</param>
/// <param name="LastWatchedAt">When the last moderator stopped watching. Null while somebody is.</param>
/// <param name="LastSeen">Who was there at <paramref name="LastWatchedAt"/>. Not "here now".</param>
/// <param name="WorldCapacity">
/// How many the world holds, as its page says, for "25/40" the way the game shows it. Null until
/// Modbot has read the world. Never a limit: exemptions raise real capacity above it.
/// </param>
/// <param name="WorldPlatforms">The platforms the world has a build for, for the game's PC, Android and iOS badges.</param>
/// <param name="HeadCountUnsure">
/// True when <paramref name="HeadCount"/> came from the page's <c>n_users</c> because it had no
/// <c>userCount</c>. Shown as "80?".
/// </param>
public sealed record LiveInstanceView(
    Guid Id,
    string WorldId,
    string? WorldName,
    string? WorldImageUrl,
    string? VRChatInstanceId,
    string? InstanceName,
    string? GroupAccessType,
    string? Region,
    DateTimeOffset OpenedAt,
    int? HeadCount,
    IReadOnlyList<LiveWatcherView> Watching,
    IReadOnlyList<LivePersonView> People,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<LivePersonView> LastSeen,
    int? WorldCapacity = null,
    IReadOnlyList<string>? WorldPlatforms = null,
    bool HeadCountUnsure = false);

/// <param name="Voice">
/// The Discord server's voice channels with somebody in them, in the server's own order. Empty when
/// no Discord server is set or nobody is talking.
/// </param>
/// <param name="Tally">What has happened since the oldest open instance opened. Null when none is open.</param>
public sealed record LiveView(
    IReadOnlyList<LiveInstanceView> Instances,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<LiveVoiceChannelView>? Voice = null,
    LiveTallyView? Tally = null);

/// <summary>
/// The running line along the top of the Live page: "Since 8:02 PM: 212 arrivals · 4 warns · 3 kicks · 1 ban".
/// </summary>
/// <param name="Since">When the oldest instance still open was opened: the start of tonight's event.</param>
/// <param name="Arrivals">
/// People seen walking into an open instance since then, each counted once. Only what a
/// moderator's Companion App saw, and two apps in one instance both report the same arrival.
/// </param>
/// <param name="Warns">Instance warns from the group's audit log.</param>
/// <param name="Kicks">Kicks from an instance. Removal from the group is a different action and is not counted.</param>
/// <param name="Bans">Bans from the group.</param>
public sealed record LiveTallyView(DateTimeOffset Since, int Arrivals, int Warns, int Kicks, int Bans);

/// <param name="Here">Flagged people in the group's open instances now, each counted once.</param>
public sealed record FlaggedHereCount(int Here);

/// <summary>A Discord voice channel with people in it right now.</summary>
/// <param name="Name">The channel's name, or null when the server index has not seen it.</param>
public sealed record LiveVoiceChannelView(string ChannelId, string? Name, IReadOnlyList<LiveVoiceMemberView> People);

/// <summary>Somebody in a Discord voice channel.</summary>
/// <param name="Since">When they joined it, as the bot saw it. Null when it was already so when the bot came online.</param>
public sealed record LiveVoiceMemberView(string UserId, string DisplayName, string? AvatarUrl, DateTimeOffset? Since);

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

        var live = app.MapGroup("/api/live").WithTags("Live").RequireAuthorization();

        live.MapGet("/", async (
                    [FromServices] ModbotContext db,
                    [FromServices] IModbotClock clock,
                    CancellationToken ct) =>
                Results.Ok(await ReadAsync(db, clock.UtcNow, ct)))
            .RequiresFlag(ModbotPermissions.ViewLiveInstances)
            .WithName("GetLiveInstances")
            .WithSummary("List live instances")
            .WithDescription(
                "Read from Modbot's own tables; nothing here calls VRChat. Every open group "
                + "instance is listed with its head count whether or not a moderator is in it. "
                + "`people` is filled only while a moderator's client is watching; otherwise "
                + "`lastSeen` holds who was there when watching last stopped, at `lastWatchedAt`. "
                + "`voice` lists the Discord server's voice channels with somebody in them.")
            .Produces<LiveView>()
            .Produces(StatusCodes.Status403Forbidden);

        live.MapGet("/flagged-count", async (
                    [FromServices] ModbotContext db,
                    [FromServices] IModbotClock clock,
                    CancellationToken ct) =>
                Results.Ok(new FlaggedHereCount(await FlaggedHereAsync(db, clock.UtcNow, ct))))
            .RequiresFlag(ModbotPermissions.ViewLiveInstances)
            .WithName("CountFlaggedPeopleHere")
            .WithSummary("Count flagged people in instances")
            .WithDescription(
                "How many flagged people are in the group's open instances right now, by the same "
                + "rules and the same \"who is present\" as the Live page: the number beside Live in "
                + "the sidebar. Counts only instances a moderator is watching, because nobody else "
                + "knows who is in one. Read from Modbot's own tables; nothing here calls VRChat.")
            .Produces<FlaggedHereCount>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// The number beside Live: everybody present in an open group instance whom the flag rules
    /// mark, each counted once however many instances they are in. What <see cref="ReadAsync"/>
    /// reads, less everything a count does not need.
    /// </summary>
    internal static async Task<int> FlaggedHereAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { Length: > 0 })
            return 0;

        var instances = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null)
            .ToListAsync(ct);

        if (instances.Count == 0)
            return 0;

        var people = await new InstancePeopleReader(db).ForInstancesAsync(instances, ct);

        var here = people.Values
            .SelectMany(p => p.Here)
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (here.Count == 0)
            return 0;

        var ranks = await db.VRChatUsers.AsNoTracking()
            .Where(u => here.Contains(u.UserId))
            .Select(u => new { u.UserId, u.TrustRank })
            .ToDictionaryAsync(u => u.UserId, u => u.TrustRank, StringComparer.Ordinal, ct);

        var flagged = await FlagRules.ReadAsync(db, here, ranks, now, ct);

        return here.Count(id => flagged.GetValueOrDefault(id)?.IsFlagged == true);
    }

    internal static async Task<LiveView> ReadAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Read, never created: a GET that writes the settings row is a surprise.
        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { Length: > 0 })
            return new LiveView([], now, await VoiceAsync(db, ct));

        var instances = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null)
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(ct);

        if (instances.Count == 0)
            return new LiveView([], now, await VoiceAsync(db, ct));

        var worldIds = instances.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var worlds = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.ImageUrl, w.ThumbnailImageUrl, w.Capacity, w.Platforms })
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        var people = await new InstancePeopleReader(db).ForInstancesAsync(instances, ct);

        var everyone = people.Values
            .SelectMany(p => p.Here.Concat(p.LastSeen))
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var members = await MembersAndStaff.ReadAsync(db, everyone, ct);

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

        var flagged = await FlagRules.ReadAsync(
            db,
            everyone,
            profiles.ToDictionary(p => p.Key, p => p.Value.Rank, StringComparer.Ordinal),
            now,
            ct);

        LivePersonView Person(PersonHere p)
        {
            var name = p.DisplayName ?? names.GetValueOrDefault(p.UserId);
            var described = ContextHandler.Describe(p.UserId, name, flagged, members);

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
                instance.Name,
                instance.GroupAccessType,
                instance.Region,
                instance.OpenedAt,
                HeadCounts.Shown(instance),
                inInstance.Watching
                    .Select(w => new LiveWatcherView(w.UserId, w.DisplayName ?? names.GetValueOrDefault(w.UserId), w.Since))
                    .ToList(),
                inInstance.Here.Select(Person).ToList(),
                inInstance.LastWatchedAt,
                inInstance.LastSeen.Select(Person).ToList(),
                world?.Capacity,
                Places.InstanceRows.PlatformsOf(world?.Platforms),
                HeadCounts.ShownUnsure(instance));
        }).ToList();

        return new LiveView(views, now, await VoiceAsync(db, ct), await TallyAsync(db, instances, ct));
    }

    /// <summary>
    /// Counts for the running line, from the oldest open instance's opening. Two queries on the
    /// fact table over a few hours, so cheap enough for a page that asks every half minute.
    /// </summary>
    internal static async Task<LiveTallyView> TallyAsync(
        ModbotContext db,
        IReadOnlyCollection<VRChatInstance> open,
        CancellationToken ct)
    {
        var since = open.Min(i => i.OpenedAt);
        var numbers = open
            .Select(i => i.VRChatInstanceId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var arrivals = numbers.Count == 0
            ? 0
            : await db.Events.AsNoTracking()
                .Where(e => e.Type == FactType.InstanceJoined
                    && e.OccurredAt >= since
                    && e.InstanceId != null
                    && numbers.Contains(e.InstanceId))
                .Select(e => e.SubjectId)
                .Distinct()
                .CountAsync(ct);

        var actions = await db.Events.AsNoTracking()
            .Where(e => e.OccurredAt >= since
                && (e.Type == FactType.GroupInstanceWarn
                    || e.Type == FactType.GroupInstanceKick
                    || e.Type == FactType.MemberBanned))
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Type, g => g.Count, StringComparer.Ordinal, ct);

        return new LiveTallyView(
            since,
            arrivals,
            actions.GetValueOrDefault(FactType.GroupInstanceWarn),
            actions.GetValueOrDefault(FactType.GroupInstanceKick),
            actions.GetValueOrDefault(FactType.MemberBanned));
    }

    /// <summary>
    /// Who is in which Discord voice channel, from the member rows the bot keeps: it sets a member's
    /// channel as they join, move and leave, and puts it right again whenever it reconnects. Nothing
    /// here asks Discord.
    /// </summary>
    internal static async Task<IReadOnlyList<LiveVoiceChannelView>> VoiceAsync(ModbotContext db, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        if (guildId is not { Length: > 0 })
            return [];

        var talking = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.VoiceChannelId != null && m.LeftAt == null)
            .Select(m => new { m.UserId, m.DisplayName, m.AvatarUrl, m.VoiceChannelId, m.VoiceSince })
            .ToListAsync(ct);

        if (talking.Count == 0)
            return [];

        var channelIds = talking.Select(m => m.VoiceChannelId!).Distinct(StringComparer.Ordinal).ToList();
        var channels = await db.DiscordChannels.AsNoTracking()
            .Where(c => c.GuildId == guildId && channelIds.Contains(c.ChannelId))
            .Select(c => new { c.ChannelId, c.Name, c.Position })
            .ToDictionaryAsync(c => c.ChannelId, StringComparer.Ordinal, ct);

        return talking
            .GroupBy(m => m.VoiceChannelId!, StringComparer.Ordinal)
            .OrderBy(g => channels.GetValueOrDefault(g.Key)?.Position ?? int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new LiveVoiceChannelView(
                g.Key,
                channels.GetValueOrDefault(g.Key)?.Name,
                g.OrderBy(m => m.VoiceSince ?? DateTimeOffset.MinValue)
                    .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new LiveVoiceMemberView(m.UserId, m.DisplayName, m.AvatarUrl, m.VoiceSince))
                    .ToList()))
            .ToList();
    }
}
