using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Places;

/// <summary>
/// World and instance rows, written the way the sweeps write them.
/// </summary>
/// <remarks>
/// Straight into the tables rather than through the producers, because these tests are about what
/// the read endpoints make of a row and not about how the row got there — and the producers that
/// write them need a live VRChat gate.
/// </remarks>
internal static class PlacesFixtures
{
    public static async Task<VRChatWorld> WorldAsync(
        ReadSurfaceTestHost host,
        string worldId,
        string? name,
        DateTimeOffset seenAt,
        CancellationToken ct)
    {
        var world = new VRChatWorld
        {
            WorldId = worldId,
            Name = name,
            AuthorName = name is null ? null : "Somebody",
            Capacity = name is null ? null : 32,
            ReleaseStatus = name is null ? null : "public",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            LastRefreshedAt = name is null ? null : seenAt,
        };

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.VRChatWorlds.Add(world);
        await db.SaveChangesAsync(ct);

        return world;
    }

    public static async Task<VRChatInstance> InstanceAsync(
        ReadSurfaceTestHost host,
        string worldId,
        string number,
        DateTimeOffset openedAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset? closedAt,
        CancellationToken ct,
        string? name = null)
    {
        var instance = new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = $"{worldId}:{number}",
            WorldId = worldId,
            VRChatInstanceId = number,
            Name = name,
            GroupId = "grp_1",
            Type = "group",
            GroupAccessType = "plus",
            Region = "us",
            OpenedAt = openedAt,
            LastSeenAt = lastSeenAt,
            ClosedAt = closedAt,
            ClosedBy = closedAt is null ? null : "list",
            LastUserCount = 3,
            PeakUserCount = 7,
            SeenInGroupList = true,
        };

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.VRChatInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        return instance;
    }

    public static async Task PersonAsync(
        ReadSurfaceTestHost host,
        string userId,
        string displayName,
        DateTimeOffset seenAt,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.VRChatUsers.Add(new VRChatUser
        {
            UserId = userId,
            DisplayName = displayName,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            LastRefreshedAt = seenAt,
        });

        await db.SaveChangesAsync(ct);
    }
}
