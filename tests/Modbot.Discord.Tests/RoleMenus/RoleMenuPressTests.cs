using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.RoleMenus;
using Modbot.Discord.Tests.Sync;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.RoleMenus;

/// <summary>
/// Pressing a role menu: the role is given, or taken back when already held; a pick-one menu takes
/// the other roles away; a role that is dangerous, managed, gone from the server or no longer on the
/// menu is never given, and each change is written down as a fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RoleMenuPressTests
{
    private const string Member = "5001";
    private const string Red = "811";
    private const string Blue = "812";
    private const string Green = "813";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private readonly PostgresFixture _db;

    public RoleMenuPressTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Reply(DiscordReply? reply) => reply?.Text ?? string.Empty;

    private async Task<(TestServices Services, Guid MenuId)> SetUpAsync(
        bool singleChoice = false,
        string style = DiscordRoleMenuStyles.Buttons,
        Action<DiscordRole>? changeBlue = null,
        bool blueOnMenu = true)
    {
        var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await using var db = services.Database.NewContext();

        foreach (var (id, name) in new[] { (Red, "Red"), (Blue, "Blue"), (Green, "Green") })
        {
            var role = new DiscordRole
            {
                RoleId = id,
                GuildId = SyncSetUp.Guild,
                Name = name,
                BotCanAssign = true,
                Permissions = Community,
                FirstSeenAt = now,
                UpdatedAt = now,
            };

            if (id == Blue)
                changeBlue?.Invoke(role);

            db.DiscordRoles.Add(role);
        }

        var menu = new DiscordRoleMenu
        {
            Name = "Colours",
            ChannelId = "900",
            Text = "Pick a colour",
            Style = style,
            SingleChoice = singleChoice,
            MessageId = "1234",
            CreatedAt = now,
            UpdatedAt = now,
            PostedAt = now,
        };

        menu.Roles.Add(new DiscordRoleMenuRole { MenuId = menu.Id, RoleId = Red, Label = "Red", Position = 0 });

        if (blueOnMenu)
            menu.Roles.Add(new DiscordRoleMenuRole { MenuId = menu.Id, RoleId = Blue, Label = "Blue", Position = 1 });

        menu.Roles.Add(new DiscordRoleMenuRole { MenuId = menu.Id, RoleId = Green, Label = "Green", Position = 2 });

        db.DiscordRoleMenus.Add(menu);
        await db.SaveChangesAsync(Ct);

        return (services, menu.Id);
    }

    private static async Task<DiscordReply> PressAsync(
        TestServices services,
        FakeGateway? gateway,
        string buttonId,
        IReadOnlyList<string>? held,
        IReadOnlyList<string>? values = null)
    {
        DiscordReply? sent = null;
        var press = new DiscordButtonPress(Member, "someone", buttonId, (r, _) =>
        {
            sent = r;
            return Task.CompletedTask;
        })
        {
            MemberRoleIds = held,
            Values = values ?? [],
        };

        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<RoleMenuPress>().HandleAsync(gateway, press, Ct);
    }

    [Fact]
    public async Task A_button_gives_a_role_the_member_does_not_hold()
    {
        var (services, menu) = await SetUpAsync();
        await using var _ = services;
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Red), held: []);

        Assert.Equal([(true, SyncSetUp.Guild, Member, Red)], gateway.RoleChanges);
        Assert.Contains("Red", Reply(reply));
        Assert.Equal("Modbot: role menu “Colours”", Assert.Single(gateway.RoleReasons));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.RoleMenuRoleGiven, Ct));
        Assert.Equal(Member, fact.SubjectId);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(Red, JsonDocument.Parse(fact.Data ?? "{}").RootElement.GetProperty("roleId").GetString());
    }

    [Fact]
    public async Task A_button_takes_back_a_role_the_member_holds()
    {
        var (services, menu) = await SetUpAsync();
        await using var _ = services;
        var gateway = new FakeGateway();

        await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Red), held: [Red]);

        Assert.Equal([(false, SyncSetUp.Guild, Member, Red)], gateway.RoleChanges);
        Assert.Single(await services.FactsOfTypeAsync(FactType.RoleMenuRoleTaken, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.RoleMenuRoleGiven, Ct));
    }

    [Fact]
    public async Task A_menu_that_is_not_pick_one_leaves_the_other_roles_alone()
    {
        var (services, menu) = await SetUpAsync(singleChoice: false);
        await using var _ = services;
        var gateway = new FakeGateway();

        await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: [Red]);

        Assert.Equal([(true, SyncSetUp.Guild, Member, Blue)], gateway.RoleChanges);
    }

    [Fact]
    public async Task A_pick_one_menu_takes_the_other_menu_roles_away_but_not_other_roles()
    {
        var (services, menu) = await SetUpAsync(singleChoice: true);
        await using var _ = services;
        var gateway = new FakeGateway();

        await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: [Red, "999"]);

        Assert.Equal(
            [(true, SyncSetUp.Guild, Member, Blue), (false, SyncSetUp.Guild, Member, Red)],
            gateway.RoleChanges);
    }

    [Fact]
    public async Task A_pick_one_menu_keeps_the_old_role_when_the_new_one_is_refused()
    {
        var (services, menu) = await SetUpAsync(singleChoice: true, changeBlue: r => r.Permissions = Administrator);
        await using var _ = services;
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: [Red]);

        Assert.Empty(gateway.RoleChanges);
        Assert.Contains("Blue", Reply(reply));
    }

    [Fact]
    public async Task A_list_toggles_each_role_that_was_picked()
    {
        var (services, menu) = await SetUpAsync(style: DiscordRoleMenuStyles.Select);
        await using var _ = services;
        var gateway = new FakeGateway();

        await PressAsync(services, gateway, RoleMenuMessage.ListId(menu), held: [Green], values: [Red, Green]);

        Assert.Equal(
            [(true, SyncSetUp.Guild, Member, Red), (false, SyncSetUp.Guild, Member, Green)],
            gateway.RoleChanges);
    }

    [Fact]
    public async Task A_role_with_a_dangerous_permission_is_never_given()
    {
        var (services, menu) = await SetUpAsync(changeBlue: r => r.Permissions = Community | Administrator);
        await using var _ = services;
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: []);

        Assert.Empty(gateway.RoleChanges);
        Assert.Contains("Blue", Reply(reply));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.RoleMenuRoleGiven, Ct));
    }

    [Fact]
    public async Task A_dangerous_role_can_still_be_handed_back()
    {
        var (services, menu) = await SetUpAsync(changeBlue: r => r.Permissions = Administrator);
        await using var _ = services;
        var gateway = new FakeGateway();

        await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: [Blue]);

        Assert.Equal([(false, SyncSetUp.Guild, Member, Blue)], gateway.RoleChanges);
    }

    [Fact]
    public async Task A_role_owned_by_a_bot_a_role_with_unread_permissions_and_a_removed_role_are_refused()
    {
        foreach (var change in new Action<DiscordRole>[]
                 {
                     r => r.Managed = true,
                     r => r.Permissions = null,
                     r => r.RemovedAt = r.UpdatedAt,
                     r => r.Everyone = true,
                 })
        {
            var (services, menu) = await SetUpAsync(changeBlue: change);
            await using var _ = services;
            var gateway = new FakeGateway();

            await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: []);

            Assert.Empty(gateway.RoleChanges);
        }
    }

    [Fact]
    public async Task A_role_dropped_from_the_menu_since_it_was_posted_is_ignored()
    {
        var (services, menu) = await SetUpAsync(blueOnMenu: false);
        await using var _ = services;
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Blue), held: []);

        Assert.Empty(gateway.RoleChanges);
        Assert.False(string.IsNullOrWhiteSpace(Reply(reply)));
    }

    [Fact]
    public async Task A_menu_that_was_deleted_says_so_and_changes_nothing()
    {
        var (services, _) = await SetUpAsync();
        await using var _ = services;
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(Guid.CreateVersion7(), Red), held: []);

        Assert.Empty(gateway.RoleChanges);
        Assert.False(string.IsNullOrWhiteSpace(Reply(reply)));
    }

    [Fact]
    public async Task Without_a_connection_to_Discord_nothing_is_changed()
    {
        var (services, menu) = await SetUpAsync();
        await using var _ = services;

        var reply = await PressAsync(services, gateway: null, RoleMenuMessage.ButtonId(menu, Red), held: []);

        Assert.Contains("not connected", Reply(reply));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.RoleMenuRoleGiven, Ct));
    }

    [Fact]
    public async Task A_failure_from_Discord_is_quoted_and_nothing_is_written_down()
    {
        var (services, menu) = await SetUpAsync();
        await using var _ = services;
        var gateway = new FakeGateway { RoleError = "Missing Permissions" };

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Red), held: []);

        Assert.Contains("Missing Permissions", Reply(reply));
        Assert.Contains("Red", Reply(reply));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.RoleMenuRoleGiven, Ct));
    }

    [Fact]
    public async Task A_member_who_has_left_the_server_is_told_so()
    {
        var (services, menu) = await SetUpAsync();
        await using var _ = services;
        var gateway = new FakeGateway();
        gateway.NotInServer.Add(Member);

        var reply = await PressAsync(services, gateway, RoleMenuMessage.ButtonId(menu, Red), held: []);

        Assert.Contains("not in the server", Reply(reply));
        Assert.Empty(gateway.RoleChanges);
    }
}
