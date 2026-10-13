using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Permissions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Permissions;

/// <summary>
/// The permission watch: a role or a member gaining a dangerous permission is a fact and a post to
/// the staff channel, and, only when the setting is on, is taken away again.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordPermissionWatchTests
{
    private const long Administrator = 1L << 3;
    private const long ManageMessages = 1L << 13;
    private const long SendMessages = 1L << 11;
    private const string StaffChannel = "555";
    private const string Plain = "201";
    private const string Admin = "301";
    private const string BotRole = "302";

    private readonly PostgresFixture _db;

    public DiscordPermissionWatchTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordRoleSnapshot Role(string id, string name, long permissions, bool managed = false)
        => new(id, name, 0, 1, managed, Everyone: false, BotCanAssign: !managed, permissions);

    private static DiscordMemberSnapshot Member(string id, string name = "Ada", bool isBot = false, params string[] roles)
        => new(id, name.ToLowerInvariant(), name, null, isBot, null, roles, null);

    private static FakeGateway Gateway()
        => new FakeGatewayFactory().Next(g => g.Server = new DiscordServerSnapshot(
            Guild,
            "The Black Cat",
            BotCanViewAuditLog: true,
            BotCanManageRoles: true,
            [],
            [
                new DiscordRoleSnapshot(Guild, "@everyone", 0, 0, Managed: false, Everyone: true, BotCanAssign: false, SendMessages),
                Role(Plain, "Member", SendMessages),
                Role(Admin, "Head Mod", Administrator | ManageMessages),
                Role(BotRole, "Helper bot", Administrator, managed: true),
            ]));

    private static async Task SetUpAsync(TestServices services, bool removes, string? channel = StaffChannel)
        => await services.ConfigureAsync(s =>
        {
            s.DiscordPermissionWatchChannelId = channel;
            s.DiscordPermissionWatchRemoves = removes;
        }, Ct);

    /// <summary>The member is stored with the roles they held last time, as the recorder keeps them.</summary>
    private static async Task StoreMemberAsync(TestServices services, string id, params string[] roles)
    {
        await using var db = services.Database.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = id,
            Username = "u" + id,
            DisplayName = "u" + id,
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.Events.AsNoTracking()
            .Where(e => e.Type.StartsWith("discord.permission."))
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static string? Data(ModbotEvent fact, string key) => JsonNode.Parse(fact.Data)?[key]?.ToString();

    private static async Task RolesGainedAsync(TestServices services, FakeGateway gateway, params DiscordRoleGain[] gains)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<DiscordPermissionWatch>()
            .RolesGainedAsync(gateway, Guild, gains, Ct);
    }

    private static async Task MemberChangedAsync(
        TestServices services, FakeGateway gateway, DiscordMemberSnapshot member, bool joined = false)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<DiscordPermissionWatch>()
            .MemberChangedAsync(gateway, Guild, member, joined, Ct);
    }

    private static string PostedText(FakeGateway gateway)
    {
        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        return card.Description + " | " + string.Join(" | ", card.Fields.Select(f => f.Value));
    }

    [Fact]
    public async Task ARoleThatGainedAdministrator_IsAFactAndAPost_AndNothingIsRemovedByDefault()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: false);
        var gateway = Gateway();

        await RolesGainedAsync(services, gateway, new DiscordRoleGain(Admin, "Head Mod", Managed: false, Administrator, Created: false));

        var fact = Assert.Single(await FactsAsync(services));
        Assert.Equal(FactType.DiscordRoleGainedPermission, fact.Type);
        Assert.Equal(Admin, fact.SubjectId);
        Assert.Equal("Administrator", Data(fact, "permissions"));
        Assert.Equal("kept", Data(fact, "action"));

        Assert.Equal(StaffChannel, Assert.Single(gateway.Posts).ChannelId);
        Assert.Contains("Head Mod", PostedText(gateway));
        Assert.Contains("Left as it was", PostedText(gateway));
        Assert.Empty(gateway.PermissionRemovals);
    }

    [Fact]
    public async Task WithNoStaffChannel_TheFactIsKept_AndNothingIsPosted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: false, channel: null);
        var gateway = Gateway();

        await RolesGainedAsync(services, gateway, new DiscordRoleGain(Admin, "Head Mod", Managed: false, Administrator, Created: false));

        Assert.Single(await FactsAsync(services));
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task ARoleThatGainedAPermission_WithRemovalOn_LosesExactlyThosePermissions()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        var gateway = Gateway();

        await RolesGainedAsync(services, gateway, new DiscordRoleGain(Admin, "Head Mod", Managed: false, Administrator | ManageMessages, Created: false));

        var removal = Assert.Single(gateway.PermissionRemovals);
        Assert.Equal((Guild, Admin, Administrator | ManageMessages), (removal.GuildId, removal.RoleId, removal.Permissions));
        Assert.Equal(DiscordPermissionWatch.Reason, removal.Reason);

        var fact = Assert.Single(await FactsAsync(services));
        Assert.Equal("removed", Data(fact, "action"));
        Assert.Contains("Permission removed", PostedText(gateway));
    }

    [Fact]
    public async Task WhenDiscordRefusesTheRemoval_TheFactAndThePostSaySo()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        var gateway = Gateway();
        gateway.RoleError = "Missing Permissions";

        await RolesGainedAsync(services, gateway, new DiscordRoleGain(Admin, "Head Mod", Managed: false, Administrator, Created: false));

        var fact = Assert.Single(await FactsAsync(services));
        Assert.Equal("kept", Data(fact, "action"));
        Assert.Equal("Missing Permissions", Data(fact, "error"));
        Assert.Contains("Could not remove", PostedText(gateway));
    }

    [Fact]
    public async Task ANewRole_IsSaidToHaveBeenMadeWithTheirPermission()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: false);
        var gateway = Gateway();

        await RolesGainedAsync(services, gateway, new DiscordRoleGain(Admin, "Head Mod", Managed: false, Administrator, Created: true));

        Assert.Contains("was made with", PostedText(gateway));
    }

    [Fact]
    public async Task AMemberGivenAWatchedRole_IsAFactAndAPost_AndKeepsTheRoleByDefault()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: false);
        await StoreMemberAsync(services, "7", Plain);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("7", "Ada", isBot: false, Plain, Admin));

        var fact = Assert.Single(await FactsAsync(services));
        Assert.Equal(FactType.DiscordMemberGainedPermission, fact.Type);
        Assert.Equal("7", fact.SubjectId);
        Assert.Equal(Admin, Data(fact, "roleId"));
        Assert.Equal("Administrator, Manage Messages", Data(fact, "permissions"));
        Assert.Equal("kept", Data(fact, "action"));

        Assert.Contains("Ada", PostedText(gateway));
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task AMemberGivenAWatchedRole_WithRemovalOn_LosesTheRole()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        await StoreMemberAsync(services, "7", Plain);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("7", "Ada", isBot: false, Plain, Admin));

        var change = Assert.Single(gateway.RoleChanges);
        Assert.Equal((false, Guild, "7", Admin), change);
        Assert.Equal("removed", Data(Assert.Single(await FactsAsync(services)), "action"));
    }

    [Fact]
    public async Task TheServersOwner_IsToldAbout_ButNeverLosesTheRole()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        await StoreMemberAsync(services, "7", Plain);
        var gateway = Gateway();
        gateway.OwnerId = "7";

        await MemberChangedAsync(services, gateway, Member("7", "Ada", isBot: false, Plain, Admin));

        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(gateway.PermissionRemovals);
        Assert.Equal("kept", Data(Assert.Single(await FactsAsync(services)), "action"));
        Assert.Contains("Server owner", PostedText(gateway));
    }

    [Fact]
    public async Task ARoleModbotJustGave_IsNotNews()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        await StoreMemberAsync(services, "7", Plain);
        var gateway = Gateway();
        gateway.GivenByModbot.Add(("7", Admin));

        await MemberChangedAsync(services, gateway, Member("7", "Ada", isBot: false, Plain, Admin));

        Assert.Empty(await FactsAsync(services));
        Assert.Empty(gateway.Posts);
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task ModbotsOwnAccount_IsNeverWatched()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member(gateway.BotUserId!, "Modbot", isBot: true, BotRole), joined: true);

        Assert.Empty(await FactsAsync(services));
    }

    [Fact]
    public async Task ABotsIntegrationRole_HasItsPermissionsTakenOffTheRole_NotTheRoleOffTheBot()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("8", "Helper", isBot: true, BotRole), joined: true);

        Assert.Empty(gateway.RoleChanges);
        var removal = Assert.Single(gateway.PermissionRemovals);
        Assert.Equal((BotRole, Administrator), (removal.RoleId, removal.Permissions));
        Assert.Contains("(bot)", PostedText(gateway));
    }

    [Fact]
    public async Task AMemberWhoseRolesWereNeverStored_IsSkipped()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("9", "Cy", isBot: false, Admin));

        Assert.Empty(await FactsAsync(services));
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task ARoleWithNoWatchedPermission_AndARoleTheMemberAlreadyHad_AreNotNews()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: true);
        await StoreMemberAsync(services, "7", Admin);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("7", "Ada", isBot: false, Admin, Plain));

        Assert.Empty(await FactsAsync(services));
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task ANewMember_WithAWatchedRole_IsToldAboutWithoutAStoredRow()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetUpAsync(services, removes: false);
        var gateway = Gateway();

        await MemberChangedAsync(services, gateway, Member("10", "Di", isBot: false, Admin), joined: true);

        Assert.Equal(FactType.DiscordMemberGainedPermission, Assert.Single(await FactsAsync(services)).Type);
    }
}
