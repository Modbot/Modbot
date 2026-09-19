using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Users;
using Modbot.TestSupport;
using Modbot.VRChat.Invites;
using Modbot.VRChat.Tests.Sync;

namespace Modbot.VRChat.Tests.Invites;

/// <summary>
/// Auto-invites over a real database, a real gate and a scripted VRChat (auto-invites design).
/// </summary>
/// <remarks>
/// Built on the sync base for the reason the calendar's suite is: the things worth proving here —
/// that the thirty seconds survives a restart, that a person is never invited twice, that nothing
/// happens without a companion reporting — are properties of the database and the gate, and a test
/// that substituted either would only assert that the pass calls the method it calls.
/// </remarks>
public abstract class AutoInviteTestBase(PostgresFixture fixture) : SyncTestBase(fixture)
{
    protected const string Instance = "41337";
    protected const string World = "wrld_auto_invite";
    protected const string Moderator = "usr_moderator";
    protected const string Stranger = "usr_stranger";

    protected static readonly Guid Device = Guid.Parse("9f1c2a4e-0000-4000-8000-000000000001");

    /// <summary>One pass, in its own scope, the way the hosted service runs it.</summary>
    protected async Task<bool> RunAsync()
    {
        await using var context = Database.NewContext();

        var invites = new GroupInvites(Gate, context, Clock);

        return await new GroupAutoInvites(
            context,
            new GiveawayRuleChecker(context, Clock),
            invites,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            Clock).RunOnceAsync(Ct);
    }

    /// <summary>Switches the feature on and sets the rules. Off until a test calls this.</summary>
    protected async Task SwitchOnAsync(
        GiveawayRule? rules = null, int minutes = 5, int againAfterDays = 30)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        settings.GroupAutoInviteEnabled = true;
        settings.GroupAutoInviteMinutesInInstance = minutes;
        settings.GroupAutoInviteAgainAfterDays = againAfterDays;
        settings.GroupAutoInviteRules = GiveawayRules.Store(rules ?? GiveawayRule.Everyone);

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>The instance row the group's own list would have written.</summary>
    protected async Task OpenInstanceAsync(DateTimeOffset? openedAt = null)
    {
        await using var context = Database.NewContext();

        context.VRChatInstances.Add(new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = $"{World}:{Instance}~group({GroupId})",
            WorldId = World,
            VRChatInstanceId = Instance,
            GroupId = GroupId,
            Type = "group",
            OpenedAt = openedAt ?? Now.AddHours(-1),
            LastSeenAt = Clock.UtcNow,
            SeenInGroupList = true,
        });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// A companion reporting from the instance: the moderator's own arrival, then the person's.
    /// </summary>
    /// <remarks>
    /// The moderator's fact is what starts the watch, and without it there is no roster at all —
    /// which is the coverage rule, not an inconvenience (design §8).
    /// </remarks>
    protected async Task WatchedArrivalAsync(
        string userId, DateTimeOffset arrivedAt, DateTimeOffset? watchFrom = null)
    {
        await FactsAsync(
            Presence(FactType.InstanceJoined, Moderator, watchFrom ?? arrivedAt, Device),
            Presence(FactType.InstanceJoined, userId, arrivedAt, Device));
    }

    /// <summary>The same arrival with nobody reporting it: an instance no companion is in.</summary>
    protected Task UnwatchedArrivalAsync(string userId, DateTimeOffset arrivedAt)
        => FactsAsync(Presence(FactType.InstanceJoined, userId, arrivedAt, reportedBy: null));

    /// <summary>A person Modbot has a profile for, so the rules about one can be answered.</summary>
    protected async Task AddPersonAsync(
        string userId,
        int? accountDays = null,
        TrustRank? trustRank = null,
        bool verified18Plus = false)
    {
        await using var context = Database.NewContext();

        context.VRChatUsers.Add(new VRChatUser
        {
            UserId = userId,
            DisplayName = userId,
            DateJoined = accountDays is { } days
                ? DateOnly.FromDateTime(Now.AddDays(-days).UtcDateTime)
                : null,
            TrustRank = trustRank,
            Is18PlusVerified = verified18Plus,
            FirstSeenAt = Now,
            LastSeenAt = Now,
        });

        await context.SaveChangesAsync(Ct);
    }

    protected async Task AddMemberAsync(string userId)
    {
        await using var context = Database.NewContext();

        context.GroupMembers.Add(new GroupMember
        {
            GroupId = GroupId,
            UserId = userId,
            Roles = "[]",
            JoinedAt = Now.AddDays(-1),
            FirstSeenAt = Now,
            LastSeenAt = Now,
        });

        await context.SaveChangesAsync(Ct);
    }

    protected async Task AddBanAsync(string userId)
    {
        await using var context = Database.NewContext();

        context.GroupBans.Add(new GroupBan
        {
            GroupId = GroupId,
            UserId = userId,
            BannedAt = Now.AddDays(-2),
            FirstSeenAt = Now,
            LastSeenAt = Now,
        });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>The companion that reports, and the Modbot account it was issued to.</summary>
    protected async Task AddCompanionAsync()
    {
        await using var context = Database.NewContext();

        var user = new ModbotUser
        {
            Id = Guid.CreateVersion7(),
            Username = "mod",
            PasswordHash = "x",
            VRChatUserId = Moderator,
            CreatedAt = Now,
        };

        context.Users.Add(user);

        context.CompanionDevices.Add(new CompanionDeviceRecord
        {
            Id = Device,
            IssuedToUserId = user.Id,
            TokenHash = "x",
            IssuedAt = Now,
            LastSeenAt = Now,
        });

        await context.SaveChangesAsync(Ct);
    }

    protected async Task<GroupAutoInvite?> InviteRowAsync(string userId)
    {
        await using var context = Database.NewContext();
        return await context.GroupAutoInvites.AsNoTracking().SingleOrDefaultAsync(i => i.UserId == userId, Ct);
    }

    /// <summary>
    /// One presence fact. <paramref name="reportedBy"/> null means no client reported it, which is
    /// a fact that can never start a watch.
    /// </summary>
    private static FactRecord Presence(string type, string userId, DateTimeOffset at, Guid? reportedBy)
    {
        var data = new JsonObject();

        if (reportedBy is { } device)
            data[ClientReport.DeviceIdKey] = device.ToString();

        return new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            WorldId = World,
            InstanceId = Instance,
            Source = FactSource.Client,
            Data = data,
        };
    }

    private async Task FactsAsync(params FactRecord[] facts)
    {
        await using var context = Database.NewContext();
        await new EventPartitionMaintainer(context, Clock).EnsureAsync(Ct);
        await new FactWriter(context, Clock).WriteManyAsync(facts, Ct);
    }
}
