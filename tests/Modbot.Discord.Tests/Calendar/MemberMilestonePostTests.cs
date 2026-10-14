using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The member milestone posts and the Regular role: each moment is acted on once, only when it was
/// reached after the posts or the role were turned on, a refusal that may pass is tried again, and the
/// role goes only to a Regular with a linked account who is still in the server.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MemberMilestonePostTests(PostgresFixture db)
{
    private const string Channel = "333333333333333333";
    private const string Guild = "424242";
    private const string RegularRole = "777";
    private const string DiscordUser = "5050";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> RunAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new MemberMilestonePost(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    /// <summary>First and fifth event, posts for the first event and the new Regulars, turned on an hour ago.</summary>
    private static Task TurnOnAsync(TestServices services, Action<Settings>? shape = null) =>
        services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.MilestoneChannelId = Channel;
            s.MilestonePostFirst = true;
            s.MilestonePostRegular = true;
            s.MilestonesFrom = services.Clock.UtcNow - TimeSpan.FromHours(1);
            shape?.Invoke(s);
        }, Ct);

    /// <summary>A person's dates counted, each one at the moment given.</summary>
    private static async Task CameAsync(TestServices services, string userId, string name, int dates, DateTimeOffset countedAt)
    {
        await using var context = services.Database.NewContext();

        for (var i = 0; i < dates; i++)
        {
            context.EventAttendees.Add(new EventAttendee
            {
                UserId = userId,
                EventId = Guid.CreateVersion7(),
                OccurrenceStartsAt = countedAt - TimeSpan.FromDays(i + 1),
                CountedAt = countedAt,
            });
        }

        if (!await context.VRChatUsers.AnyAsync(u => u.UserId == userId, Ct))
            context.VRChatUsers.Add(new VRChatUser { UserId = userId, DisplayName = name });

        await context.SaveChangesAsync(Ct);
    }

    private static async Task LinkedAsync(TestServices services, string userId, bool inServer = true)
    {
        await using var context = services.Database.NewContext();

        context.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = DiscordUser,
            VRChatUserId = userId,
            LinkedAt = services.Clock.UtcNow,
        });

        context.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = DiscordUser,
            Username = "casey",
            DisplayName = "Casey",
            FirstSeenAt = services.Clock.UtcNow,
            LeftAt = inServer ? null : services.Clock.UtcNow,
        });

        context.DiscordRoles.Add(new DiscordRole
        {
            RoleId = RegularRole,
            GuildId = Guild,
            Name = "Regular",
            Permissions = 0,
            BotCanAssign = true,
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });

        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AFirstEventIsPostedOnce_NamingEveryoneWhoReachedIt()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);
        await CameAsync(services, "usr_b", "Bo", 1, services.Clock.UtcNow);

        Assert.Equal(1, await RunAsync(services, gateway));
        Assert.Equal(0, await RunAsync(services, gateway));

        var post = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, post.ChannelId);
        var card = Assert.Single(post.Embeds);
        Assert.Equal("Came to their 1st event", card.Title);
        Assert.Equal("Ada, Bo", card.Description);
    }

    [Fact]
    public async Task AMomentReachedBeforeThePostsWereTurnedOn_IsLeftAlone()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 5, services.Clock.UtcNow - TimeSpan.FromDays(30));

        Assert.Equal(0, await RunAsync(services, gateway));
        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task APostSwitchedOffStillClaimsTheMoment_SoSwitchingItOnLaterDoesNotPostForThem()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services, s => s.MilestonePostFirst = false);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);

        Assert.Equal(0, await RunAsync(services, gateway));

        await services.ConfigureAsync(s => s.MilestonePostFirst = true, Ct);

        Assert.Equal(0, await RunAsync(services, gateway));
        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task ARefusalThatMayPass_GivesTheClaimBack_SoTheNextPassPostsIt()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);

        gateway.FailNextPost("Discord asked the bot to wait.");

        Assert.Equal(0, await RunAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        await using (var context = services.Database.NewContext())
            Assert.Contains("wait", (await context.GetSettingsAsync(Ct)).MilestoneProblem);

        Assert.Equal(1, await RunAsync(services, gateway));
        Assert.Single(gateway.Messages);

        await using var after = services.Database.NewContext();
        Assert.Null((await after.GetSettingsAsync(Ct)).MilestoneProblem);
    }

    [Fact]
    public async Task AFinalRefusalIsNotTriedAgain_AndTheCardShowsWhy()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);

        gateway.FailNextPost("Could not post to Discord: the bot may not post in that channel.", permanent: true);

        Assert.Equal(0, await RunAsync(services, gateway));
        Assert.Equal(0, await RunAsync(services, gateway));
        Assert.Empty(gateway.Messages);

        await using var context = services.Database.NewContext();
        Assert.Contains("may not post", (await context.GetSettingsAsync(Ct)).MilestoneProblem);
    }

    [Fact]
    public async Task TwoCopiesOfModbotRunningAtOnce_PostTheMomentOnce()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);

        var results = await Task.WhenAll(RunAsync(services, gateway), RunAsync(services, gateway));

        Assert.Equal(1, results.Sum());
        Assert.Single(gateway.Messages);
    }

    [Fact]
    public async Task ARegularIsPostedAtTheFifthEvent_AndTheFirstEventIsNotPostedAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services);
        await CameAsync(services, "usr_a", "Ada", 1, services.Clock.UtcNow);
        Assert.Equal(1, await RunAsync(services, gateway));

        await CameAsync(services, "usr_a", "Ada", 4, services.Clock.UtcNow);

        Assert.Equal(1, await RunAsync(services, gateway));

        Assert.Equal(["Came to their 1st event", "Now a Regular"], gateway.Messages.Select(m => Assert.Single(m.Embeds).Title));
    }

    [Fact]
    public async Task AReturnIsPostedOnce_AsWelcomeBack()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services, s =>
        {
            s.MilestonePostFirst = false;
            s.MilestonePostRegular = false;
            s.MilestonePostBack = true;
        });

        await using (var context = services.Database.NewContext())
        {
            context.VRChatUsers.Add(new VRChatUser { UserId = "usr_a", DisplayName = "Ada" });
            context.MemberMilestones.Add(new MemberMilestone
            {
                UserId = "usr_a",
                LastVisitAt = services.Clock.UtcNow,
                BackAt = services.Clock.UtcNow,
                BackAfterDays = 70,
            });
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await RunAsync(services, gateway));
        Assert.Equal(0, await RunAsync(services, gateway));

        var card = Assert.Single(Assert.Single(gateway.Messages).Embeds);
        Assert.Equal("Welcome back", card.Title);
        Assert.Equal("Ada", card.Description);
    }

    [Fact]
    public async Task TheRegularRole_GoesToARegularWithALinkedAccount_NotToOneWithout()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services, s =>
        {
            s.MilestonePostFirst = false;
            s.MilestonePostRegular = false;
            s.MilestoneRegularRoleId = RegularRole;
        });

        await LinkedAsync(services, "usr_a");
        await CameAsync(services, "usr_a", "Ada", 5, services.Clock.UtcNow);
        await CameAsync(services, "usr_b", "Bo", 5, services.Clock.UtcNow);

        await RunAsync(services, gateway);

        var change = Assert.Single(gateway.RoleChanges);
        Assert.Equal((true, Guild, DiscordUser, RegularRole), change);

        await RunAsync(services, gateway);
        Assert.Single(gateway.RoleChanges);
    }

    [Fact]
    public async Task TheRegularRole_IsNotGivenToSomebodyWhoLeftTheServer()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateway = new FakeGateway();

        await TurnOnAsync(services, s => s.MilestoneRegularRoleId = RegularRole);

        await LinkedAsync(services, "usr_a", inServer: false);
        await CameAsync(services, "usr_a", "Ada", 5, services.Clock.UtcNow);

        await RunAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
    }
}
