using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Features.Live;
using Modbot.Api.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>The Live page's read.</summary>
internal sealed class ListLiveRoomsTool : ReadTool
{
    public override string Name => "list_live_rooms";

    public override string Label => "Live instances";

    public override string Description =>
        "The group's open instances right now: world, instance number, region, head count, which "
        + "moderators are watching, and who is there while somebody is watching (otherwise who was "
        + "there when watching stopped).";

    protected override string Schema => """{"type":"object","properties":{}}""";

    public override ModbotPermissions Needs => ModbotPermissions.ViewLiveRooms;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var live = await LiveEndpoints.ReadAsync(Get<ModbotContext>(context), Get<IModbotClock>(context).UtcNow, ct);

        var references = new List<ChatReference>();
        foreach (var room in live.Rooms)
        {
            references.Add(World(room.WorldId, room.WorldName));
            references.Add(new ChatReference(
                ChatReference.Instance,
                room.Id.ToString(),
                room.VRChatInstanceId is { } n ? $"{room.WorldName ?? "Room"} #{n}" : room.WorldName));
            references.AddRange(room.People.Concat(room.LastSeen).Select(p => Person(p.UserId, p.DisplayName)));
        }

        return ChatToolResult.Json(
            new
            {
                live.GeneratedAt,
                rooms = live.Rooms.Select(r => new
                {
                    instanceId = r.Id,
                    r.WorldId,
                    r.WorldName,
                    number = r.VRChatInstanceId,
                    r.GroupAccessType,
                    r.Region,
                    r.OpenedAt,
                    r.HeadCount,
                    watching = r.Watching.Select(w => new { w.UserId, w.DisplayName, w.Since }),
                    people = r.People.Select(Person),
                    r.LastWatchedAt,
                    lastSeen = r.LastSeen.Select(Person),
                }),
            },
            references);
    }

    private static object Person(LivePersonView p) => new
    {
        p.UserId,
        p.DisplayName,
        p.ArrivedAt,
        p.HereBefore,
        p.Standing,
        p.PriorActions,
        p.Flags,
    };
}

/// <summary>Stored worlds, by id or by name.</summary>
internal sealed class FindWorldTool : ReadTool
{
    public override string Name => "find_world";

    public override string Label => "Find a world";

    public override string Description =>
        "Find VRChat worlds Modbot has seen, by exact world id or part of the world's name. Returns "
        + "up to 10 matches with their ids.";

    protected override string Schema => """
        {"type":"object","properties":{"query":{"type":"string","description":"A world id or part of a world name."}},"required":["query"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAnalytics;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "query") is not { } query)
            return ChatToolResult.Problem("query is required.");

        var pattern = Contains(query);

        var worlds = await Get<ModbotContext>(context).VRChatWorlds.AsNoTracking()
            .Where(w => w.WorldId == query || (w.Name != null && EF.Functions.ILike(w.Name, pattern, "\\")))
            .OrderByDescending(w => w.WorldId == query)
            .ThenByDescending(w => w.LastSeenAt)
            .Take(10)
            .Select(w => new { w.WorldId, w.Name, w.AuthorName, w.LastSeenAt })
            .ToListAsync(ct);

        return ChatToolResult.Json(new { worlds }, worlds.Select(w => World(w.WorldId, w.Name)));
    }
}

/// <summary>The world popup's read.</summary>
internal sealed class GetWorldTool : ReadTool
{
    public override string Name => "get_world";

    public override string Label => "Open a world";

    public override string Description =>
        "One world: its name, author, capacity, how much time people spent in it, how many rooms "
        + "have run in it and are open now, and the most recent rooms with their instanceIds.";

    protected override string Schema => """
        {"type":"object","properties":{"worldId":{"type":"string","description":"The VRChat world id."}},"required":["worldId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAnalytics;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "worldId") is not { } id)
            return ChatToolResult.Problem("worldId is required.");

        var world = await PlacesEndpoints.WorldAsync(id, Get<ModbotContext>(context), Get<IModbotClock>(context).UtcNow, ct);

        return ChatToolResult.Json(
            new
            {
                world.WorldId,
                world.Known,
                world.Name,
                world.AuthorName,
                world.Capacity,
                world.RecommendedCapacity,
                world.Tags,
                world.ReleaseStatus,
                world.FirstSeenAt,
                world.LastSeenAt,
                world.Counts,
                world.RoomsTotal,
                world.RoomsOpenNow,
                recentRooms = world.Rooms.Take(10).Select(RoomSummary),
                visitorsLast30Days = world.VisitorsPerDay.TakeLast(30),
            },
            [World(world.WorldId, world.Name), .. world.Rooms.Take(10).SelectMany(RoomReferences)]);
    }
}

/// <summary>The room popup's read, which hides who was there without ViewAuditLog.</summary>
internal sealed class GetInstanceTool : ReadTool
{
    public override string Name => "get_instance";

    public override string Label => "Open an instance";

    public override string Description =>
        "One room (instance) by Modbot's instanceId, as returned by other tools: where and when it "
        + "ran, how busy it was, who was in it and what was recorded there.";

    protected override string Schema => """
        {"type":"object","properties":{"instanceId":{"type":"string","description":"Modbot's instanceId, a GUID from another tool's result."}},"required":["instanceId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAnalytics;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (!Guid.TryParse(ChatArguments.Text(arguments, "instanceId"), out var id))
            return ChatToolResult.Problem("instanceId must be the GUID another tool returned, not VRChat's instance number.");

        var room = await PlacesEndpoints.RoomAsync(
            id, context.Held, Get<ModbotContext>(context), Get<IModbotClock>(context).UtcNow, ct);

        if (room is null)
            return ChatToolResult.Problem("No instance has that id.");

        return ChatToolResult.Json(
            new
            {
                room = RoomSummary(room.Room),
                room.Type,
                room.WorldAuthorName,
                room.WorldCapacity,
                room.LastSeenAt,
                room.Counts,
                room.CanSeeWhoWasThere,
                people = room.People,
                log = room.Log.Select(AuditSearch.Summary),
                room.LogTruncated,
            },
            [
                .. RoomReferences(room.Room),
                .. room.People.Select(p => Person(p.UserId, p.DisplayName)),
                .. AuditSearch.References(room.Log),
            ]);
    }
}

/// <summary>The My Group page's query, summed up.</summary>
internal sealed class GroupAnalyticsTool : ReadTool
{
    public override string Name => "group_analytics";

    public override string Label => "Group figures";

    public override string Description =>
        "The group's figures over the last number of days: member count at the start and end, "
        + "joins, leaves, invites and join requests, roles, how long members have been members, "
        + "and what the figures cover.";

    protected override string Schema => """
        {"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":365,"description":"How many days back, today included. Default 30."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAnalytics;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var days = ChatArguments.Number(arguments, "days", 30, 1, 365);
        var now = Get<IModbotClock>(context).UtcNow;
        var to = AnalyticsSql.DayOf(now);
        var from = to.AddDays(-(days - 1));

        var a = await new GroupAnalyticsQuery(Get<ModbotContext>(context)).RunAsync(from, to, now, ct);

        return ChatToolResult.Json(new
        {
            a.From,
            a.To,
            memberCountAtStart = a.MemberCount.FirstOrDefault(),
            memberCountAtEnd = a.MemberCount.LastOrDefault(),
            joined = a.Joined.Sum(d => d.Value),
            left = a.Left.Sum(d => d.Value),
            invitesSent = a.InvitesSent.Sum(d => d.Value),
            requestsReceived = a.RequestsReceived.Sum(d => d.Value),
            joinedPerDay = days <= 31 ? a.Joined : null,
            leftPerDay = days <= 31 ? a.Left : null,
            roles = a.Roles.Select(r => new { r.Name, r.IsModerationRole, r.Granted, r.Revoked }),
            a.Tenure,
            a.MembersWithKnownTenure,
            a.Invites,
            a.Coverage,
        });
    }
}
