using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The group's member list, roles and owner, stored the way the member sweep and the group-info
/// sync leave them -- what the roster's Member and Staff rows are read from.
/// </summary>
internal static class MemberListFixtures
{
    /// <summary>Can remove people from the group, so it counts as a moderation role.</summary>
    public const string ModeratorRole = "grol_mod";

    /// <summary>Manages the gallery and nothing else, so holding it does not make anybody staff.</summary>
    public const string SupporterRole = "grol_fan";

    /// <summary>
    /// Records the group's owner and roles, and a member sweep that started ten minutes before
    /// <paramref name="sweptAt"/> and finished at it.
    /// </summary>
    public static async Task GroupAsync(
        IServiceProvider services, string ownerId, DateTimeOffset sweptAt, CancellationToken ct)
    {
        var snapshot = new GroupInfoSnapshot(
            "Test group", null, null, null, null, ownerId, null, null, false, 100, 5,
            [
                new GroupRoleSnapshot(ModeratorRole, "Moderator", null, 1, true, false, false, false, ["group-members-remove"]),
                new GroupRoleSnapshot(SupporterRole, "Supporter", null, 2, false, false, false, false, ["group-galleries-manage"]),
            ]);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.GroupInfoSnapshot = snapshot.ToJson();
        settings.MemberSweepPreviousStartedAt = sweptAt.AddMinutes(-10);
        settings.MemberSweepCompletedAt = sweptAt;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One row of the member list, as the last sweep wrote it.</summary>
    public static GroupMember Listed(
        string groupId,
        string userId,
        DateTimeOffset seenAt,
        string? status = "member",
        DateTimeOffset? leftAt = null,
        params string[] roles) => new()
    {
        GroupId = groupId,
        UserId = userId,
        Roles = "[" + string.Join(',', roles.Order(StringComparer.Ordinal).Select(r => $"\"{r}\"")) + "]",
        JoinedAt = seenAt.AddYears(-2),
        MembershipStatus = status,
        FirstSeenAt = seenAt.AddDays(-30),
        LastSeenAt = seenAt,
        LeftAt = leftAt,
    };

    public static async Task ListAsync(IServiceProvider services, CancellationToken ct, params GroupMember[] rows)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.GroupMembers.AddRange(rows);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// No member list, no ban list, no roles and no sweep: the state before the syncs have run.
    /// The settings row is shared by every test, so a test that set these puts them back.
    /// </summary>
    public static async Task ClearAsync(IServiceProvider services, string groupId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await db.GroupMembers.Where(m => m.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GroupBans.Where(b => b.GroupId == groupId).ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.GroupInfoSnapshot = null;
        settings.MemberSweepCompletedAt = null;
        settings.MemberSweepPreviousStartedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public static async Task BanAsync(
        IServiceProvider services, string groupId, string userId, DateTimeOffset at, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.GroupBans.Add(new GroupBan
        {
            GroupId = groupId,
            UserId = userId,
            BannedAt = at,
            FirstSeenAt = at,
            LastSeenAt = at,
        });
        await db.SaveChangesAsync(ct);
    }
}
