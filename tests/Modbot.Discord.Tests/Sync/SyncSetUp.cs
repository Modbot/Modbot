using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Sync;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// The pieces every role and ban sync test needs: a server, a group, a linked person, and a way to
/// run one pass.
/// </summary>
internal static class SyncSetUp
{
    public const string Guild = "700";
    public const string Group = "grp_test";
    public const string DiscordRole = "801";
    public const string GroupRole = "grol_staff";

    public static async Task<TestServices> CreateAsync(
        PostgresFixture fixture, Action<Settings>? settings, CancellationToken ct)
    {
        var services = await TestServices.CreateAsync(fixture, ct);

        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.ManagedGroupId = Group;
            settings?.Invoke(s);
        }, ct);

        await using var db = services.Database.NewContext();

        db.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild,
            Name = "The server",
            BotCanManageRoles = true,
            BotCanBanMembers = true,
            BotCanRemoveMembers = true,
            RefreshedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });

        db.DiscordRoles.Add(new DiscordRole
        {
            RoleId = DiscordRole,
            GuildId = Guild,
            Name = "Staff",
            BotCanAssign = true,
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return services;
    }

    /// <summary>
    /// One person whose two accounts are linked, in the group and in the server.
    /// </summary>
    /// <param name="inGroup">Null leaves them out of the group entirely.</param>
    /// <param name="inServer">Null leaves them out of the Discord server entirely.</param>
    public static async Task LinkAsync(
        TestServices services,
        string vrchatUserId,
        string discordUserId,
        IReadOnlyList<string>? inGroup = null,
        IReadOnlyList<string>? inServer = null,
        CancellationToken ct = default)
    {
        await using var db = services.Database.NewContext();
        var now = services.Clock.UtcNow;

        db.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = discordUserId,
            DiscordUsername = "member" + discordUserId,
            VRChatUserId = vrchatUserId,
            VRChatDisplayName = "Person " + vrchatUserId,
            LinkedAt = now,
        });

        if (inGroup is not null)
        {
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = Group,
                UserId = vrchatUserId,
                Roles = JsonSerializer.Serialize(inGroup),
                FirstSeenAt = now,
                LastSeenAt = now,
            });
        }

        if (inServer is not null)
        {
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = discordUserId,
                DisplayName = "Member " + discordUserId,
                Username = "member" + discordUserId,
                Roles = JsonSerializer.Serialize(inServer),
                FirstSeenAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Somebody in the Discord server with no VRChat account tied to them.</summary>
    public static async Task UnlinkedMemberAsync(
        TestServices services, string discordUserId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        var now = services.Clock.UtcNow;

        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = discordUserId,
            DisplayName = "Stranger " + discordUserId,
            Username = "stranger" + discordUserId,
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(ct);
    }

    public static async Task PairAsync(TestServices services, string decides, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();

        db.DiscordRolePairs.Add(new DiscordRolePair
        {
            VRChatRoleId = GroupRole,
            VRChatRoleName = "Staff",
            DiscordRoleId = DiscordRole,
            DiscordRoleName = "Staff",
            Decides = decides,
            Enabled = true,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }

    public static async Task<RoleSyncPass> RolePassAsync(
        TestServices services, FakeGateway gateway, bool apply, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<RoleSync>().RunAsync(gateway, apply, ct);
    }

    public static async Task<BanSyncPass> BanPassAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<BanSync>().RunAsync(gateway, ct);
    }

    public static async Task<SyncPreview> CatchUpAsync(
        TestServices services, FakeGateway gateway, bool apply, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<BanSync>().CatchUpAsync(gateway, apply, ct);
    }

    public static async Task<IReadOnlyList<CopiedAction>> CopiesAsync(TestServices services, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        return await db.CopiedActions.AsNoTracking().OrderBy(c => c.StartedAt).ToListAsync(ct);
    }
}
