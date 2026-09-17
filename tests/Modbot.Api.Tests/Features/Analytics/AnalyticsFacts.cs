using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// Facts shaped the way the producers shape them, for the analytics tests.
/// </summary>
internal static class AnalyticsFacts
{
    /// <summary>An audit-log fact: exact time, VRChat actor, <c>actorDisplayName</c> in the payload.</summary>
    public static FactRecord AuditFact(
        string type,
        string subject,
        DateTimeOffset at,
        string? actor = null,
        string? actorName = null,
        string? worldId = null,
        string? instanceId = null,
        JsonObject? extra = null)
    {
        var data = extra ?? new JsonObject();
        if (actorName is not null)
            data["actorDisplayName"] = actorName;

        return new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : FactPlatform.VRChat,
            ActorId = actor,
            WorldId = worldId,
            InstanceId = instanceId,
            Source = FactSource.AuditLog,
            Data = data,
        };
    }

    /// <summary>A companion's presence report: the device that saw it, and the name it saw.</summary>
    public static FactRecord PresenceFact(
        string type,
        string subject,
        DateTimeOffset at,
        string worldId,
        string instanceId,
        string device = "device-1",
        string? name = null)
    {
        var data = new JsonObject { ["deviceId"] = device };
        if (name is not null)
            data["displayName"] = name;

        return new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = worldId,
            InstanceId = instanceId,
            Source = FactSource.Client,
            Data = data,
        };
    }

    public static FactRecord GroupInfoBaseline(int memberCount, DateTimeOffset at, int? online = null)
    {
        var baseline = new JsonObject { ["MemberCount"] = memberCount };
        if (online is not null)
            baseline["OnlineMemberCount"] = online;

        return new FactRecord
        {
            Type = FactType.GroupInfoChanged,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "grp_1",
            Source = FactSource.SyncDiff,
            Data = new JsonObject { ["baseline"] = baseline },
        };
    }

    /// <summary>A change to the online member count alone, the way the sync records one.</summary>
    public static FactRecord GroupInfoOnlineChange(int from, int to, DateTimeOffset at) => new()
    {
        Type = FactType.GroupInfoChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "grp_1",
        Source = FactSource.SyncDiff,
        Data = new JsonObject
        {
            ["changed"] = new JsonObject
            {
                ["OnlineMemberCount"] = new JsonObject { ["old"] = from, ["new"] = to },
            },
        },
    };

    public static FactRecord GroupInfoChange(int from, int to, DateTimeOffset at) => new()
    {
        Type = FactType.GroupInfoChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "grp_1",
        Source = FactSource.SyncDiff,
        Data = new JsonObject
        {
            ["changed"] = new JsonObject
            {
                ["MemberCount"] = new JsonObject { ["old"] = from, ["new"] = to },
            },
        },
    };

    public static GroupRoleSnapshot Role(string id, string name, params string[] permissions)
        => new(id, name, null, 0, false, false, false, false, permissions);

    /// <summary>Stores a group snapshot the way the group-info sync would, so the role list is known.</summary>
    public static async Task StoreGroupSnapshotAsync(
        ReadSurfaceTestHost host,
        string? ownerId,
        IReadOnlyList<GroupRoleSnapshot> roles,
        DateTimeOffset polledAt,
        CancellationToken ct)
    {
        var snapshot = new GroupInfoSnapshot(
            "Test group", null, null, null, null, ownerId, null, null, false, 100, 5, roles);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.GroupInfoSnapshot = snapshot.ToJson();
        settings.GroupInfoPolledAt = polledAt;
        await db.SaveChangesAsync(ct);
    }
}
