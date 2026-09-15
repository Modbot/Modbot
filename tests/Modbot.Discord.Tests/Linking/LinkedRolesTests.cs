using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Linking;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Linking;

/// <summary>
/// The role job: linked members get the linked role, 18+ verified ones the 18+ role, and Modbot
/// takes back only what it gave when the answer changes (Discord account linking design §6).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LinkedRolesTests
{
    private const string Guild = "700";
    private const string LinkedRole = "801";
    private const string AdultRole = "802";

    private readonly PostgresFixture _db;

    public LinkedRolesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(PostgresFixture db)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.DiscordLinkedRoleId = LinkedRole;
            s.DiscordEighteenPlusRoleId = AdultRole;
        }, Ct);
        return services;
    }

    private static async Task<DiscordAccountLink> LinkAsync(
        TestServices services, string discordUserId, string vrchatUserId, bool eighteenPlus, CancellationToken ct)
    {
        await services.AddProfileAsync(vrchatUserId, "Person " + vrchatUserId, u => u.Is18PlusVerified = eighteenPlus, ct);

        await using var db = services.Database.NewContext();
        var link = new DiscordAccountLink
        {
            DiscordUserId = discordUserId,
            DiscordUsername = "member" + discordUserId,
            VRChatUserId = vrchatUserId,
            LinkedAt = services.Clock.UtcNow,
        };
        db.DiscordAccountLinks.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    private static async Task<LinkedRolePass> PassAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<LinkedRoles>().RunAsync(gateway, Ct);
    }

    private static async Task<DiscordAccountLink> RowAsync(TestServices services, Guid id)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordAccountLinks.AsNoTracking().SingleAsync(l => l.Id == id, Ct);
    }

    [Fact]
    public async Task ALinkedMember_GetsTheLinkedRole_AndAnEighteenPlusOneAlsoGetsTheEighteenPlusRole()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var adult = await LinkAsync(services, "111", "usr_adult", eighteenPlus: true, Ct);
        var other = await LinkAsync(services, "222", "usr_other", eighteenPlus: false, Ct);

        var pass = await PassAsync(services, gateway);

        Assert.Equal(3, pass.Given);
        Assert.Contains((true, Guild, "111", LinkedRole), gateway.RoleChanges);
        Assert.Contains((true, Guild, "111", AdultRole), gateway.RoleChanges);
        Assert.Contains((true, Guild, "222", LinkedRole), gateway.RoleChanges);
        Assert.DoesNotContain((true, Guild, "222", AdultRole), gateway.RoleChanges);

        Assert.Equal(AdultRole, (await RowAsync(services, adult.Id)).EighteenPlusRoleId);
        Assert.Null((await RowAsync(services, other.Id)).EighteenPlusRoleId);

        var facts = await services.FactsOfTypeAsync(FactType.DiscordLinkRoleGranted, Ct);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactPlatform.Discord, f.SubjectPlatform));
        var adultFact = Assert.Single(facts, f => f.SubjectId == "111" && f.Data.Contains(AdultRole, StringComparison.Ordinal));
        Assert.Equal("18+", JsonDocument.Parse(adultFact.Data).RootElement.GetProperty("kind").GetString());

        // Nothing left to do: a second pass reads no rows and changes nothing.
        gateway.RoleChanges.Clear();
        var again = await PassAsync(services, gateway);
        Assert.Equal(0, again.Given + again.Removed);
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task WhenTheEighteenPlusFlagIsCleared_TheEighteenPlusRoleIsTakenAway_AndGivenBackWhenSetAgain()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        var link = await LinkAsync(services, "333", "usr_flag", eighteenPlus: true, Ct);
        await PassAsync(services, gateway);
        gateway.RoleChanges.Clear();

        await using (var db = services.Database.NewContext())
        {
            var user = await db.VRChatUsers.SingleAsync(u => u.UserId == "usr_flag", Ct);
            user.Is18PlusVerified = false;
            await db.SaveChangesAsync(Ct);
        }

        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Removed);
        Assert.Equal([(false, Guild, "333", AdultRole)], gateway.RoleChanges);
        var row = await RowAsync(services, link.Id);
        Assert.Equal(LinkedRole, row.LinkedRoleId);
        Assert.Null(row.EighteenPlusRoleId);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLinkRoleRemoved, Ct));

        await using (var db = services.Database.NewContext())
        {
            var user = await db.VRChatUsers.SingleAsync(u => u.UserId == "usr_flag", Ct);
            user.Is18PlusVerified = true;
            await db.SaveChangesAsync(Ct);
        }

        gateway.RoleChanges.Clear();
        await PassAsync(services, gateway);
        Assert.Equal([(true, Guild, "333", AdultRole)], gateway.RoleChanges);
    }

    [Fact]
    public async Task AnEndedLink_HasBothRolesTakenAway_AndKeepsItsRow()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        var link = await LinkAsync(services, "444", "usr_ended", eighteenPlus: true, Ct);
        await PassAsync(services, gateway);
        gateway.RoleChanges.Clear();

        await using (var db = services.Database.NewContext())
        {
            var row = await db.DiscordAccountLinks.SingleAsync(l => l.Id == link.Id, Ct);
            row.UnlinkedAt = services.Clock.UtcNow;
            row.UnlinkedBy = LinkEndedBy.Member;
            await db.SaveChangesAsync(Ct);
        }

        var pass = await PassAsync(services, gateway);

        Assert.Equal(2, pass.Removed);
        Assert.Contains((false, Guild, "444", LinkedRole), gateway.RoleChanges);
        Assert.Contains((false, Guild, "444", AdultRole), gateway.RoleChanges);

        var ended = await RowAsync(services, link.Id);
        Assert.NotNull(ended.UnlinkedAt);
        Assert.Null(ended.LinkedRoleId);
        Assert.Null(ended.EighteenPlusRoleId);
    }

    [Fact]
    public async Task ChangingTheLinkedRoleSetting_TakesTheOldRoleAway_AndGivesTheNewOne()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await LinkAsync(services, "555", "usr_setting", eighteenPlus: false, Ct);
        await PassAsync(services, gateway);
        gateway.RoleChanges.Clear();

        await services.ConfigureAsync(s => s.DiscordLinkedRoleId = "899", Ct);
        await PassAsync(services, gateway);

        Assert.Equal([(false, Guild, "555", LinkedRole), (true, Guild, "555", "899")], gateway.RoleChanges);
    }

    [Fact]
    public async Task AMemberNotInTheServer_IsLeftForADay()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        gateway.NotInServer.Add("666");
        var link = await LinkAsync(services, "666", "usr_away", eighteenPlus: true, Ct);

        var pass = await PassAsync(services, gateway);
        Assert.Equal(1, pass.NotInServer);
        Assert.NotNull((await RowAsync(services, link.Id)).NotInServerAt);

        gateway.NotInServer.Clear();
        await PassAsync(services, gateway);
        Assert.Empty(gateway.RoleChanges);

        services.Clock.Advance(LinkedRoles.NotInServerWait + TimeSpan.FromMinutes(1));
        await PassAsync(services, gateway);
        Assert.Contains((true, Guild, "666", LinkedRole), gateway.RoleChanges);
    }

    [Fact]
    public async Task OnceTheMemberListIsRead_OnlyPeopleInTheServerAreAskedAbout()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        var here = await LinkAsync(services, "881", "usr_listed_here", eighteenPlus: false, Ct);
        var gone = await LinkAsync(services, "882", "usr_listed_gone", eighteenPlus: false, Ct);
        var never = await LinkAsync(services, "883", "usr_never_joined", eighteenPlus: false, Ct);

        await using (var db = services.Database.NewContext())
        {
            var now = services.Clock.UtcNow;
            db.DiscordServers.Add(new DiscordServer { GuildId = Guild, Name = "Server", RefreshedAt = now, UpdatedAt = now, MembersListedAt = now });
            db.DiscordMembers.Add(new DiscordMember { GuildId = Guild, UserId = "881", Username = "here", DisplayName = "here", FirstSeenAt = now, UpdatedAt = now });
            db.DiscordMembers.Add(new DiscordMember { GuildId = Guild, UserId = "882", Username = "gone", DisplayName = "gone", FirstSeenAt = now, LeftAt = now, UpdatedAt = now });

            // Recorded as given before they left: leaving took it, so it is forgotten, not taken away.
            var row = await db.DiscordAccountLinks.SingleAsync(l => l.Id == gone.Id, Ct);
            row.LinkedRoleId = LinkedRole;
            await db.SaveChangesAsync(Ct);
        }

        var pass = await PassAsync(services, gateway);

        Assert.Equal([(true, Guild, "881", LinkedRole)], gateway.RoleChanges);
        Assert.Equal(1, pass.Given);
        Assert.Equal(LinkedRole, (await RowAsync(services, here.Id)).LinkedRoleId);
        Assert.Null((await RowAsync(services, gone.Id)).LinkedRoleId);
        Assert.Null((await RowAsync(services, never.Id)).NotInServerAt);

        // A linked person who is not in the server is a normal case: nothing is retried or reported.
        gateway.RoleChanges.Clear();
        var again = await PassAsync(services, gateway);
        Assert.Empty(gateway.RoleChanges);
        Assert.Null(again.Problem);
    }

    [Fact]
    public async Task ARefusedChange_IsReported_AndLeavesTheRowToTryAgain()

    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway { RoleError = "The bot may not change that role." };
        var link = await LinkAsync(services, "777", "usr_refused", eighteenPlus: false, Ct);

        var pass = await PassAsync(services, gateway);

        Assert.Equal("The bot may not change that role.", pass.Problem);
        var row = await RowAsync(services, link.Id);
        Assert.Null(row.LinkedRoleId);
        Assert.Equal("The bot may not change that role.", row.RoleError);

        gateway.RoleError = null;
        await PassAsync(services, gateway);
        Assert.Null((await RowAsync(services, link.Id)).RoleError);
        Assert.Equal(LinkedRole, (await RowAsync(services, link.Id)).LinkedRoleId);
    }
}
