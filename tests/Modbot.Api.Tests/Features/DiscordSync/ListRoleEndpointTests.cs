using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordSync;

/// <summary>
/// Settings → Discord → Roles from lists (roles from lists design): a staff role, @everyone and a
/// role something else gives are refused; the switch starts off; the preview names people so it
/// needs what seeing a list needs; Apply allows only the removals somebody saw; a list a role is
/// given from is in use.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ListRoleEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Guild = "9100";
    private const string Regular = "9101";
    private const string Moderator = "9102";
    private const string Unread = "9103";
    private const string Paired = "9104";

    private const ModbotPermissions SeeLists = ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;
    private const ModbotPermissions SetUp = ModbotPermissions.ManageDiscordSync | ModbotPermissions.ManageSettings;
    private const ModbotPermissions Everything =
        SetUp | SeeLists | ModbotPermissions.RunDiscordSync | ModbotPermissions.ManageLists;

    /// <summary>"In the VRChat group".</summary>
    private const string InGroup = """{"kind":"allOf","rules":[{"kind":"inGroup"}]}""";

    private async Task<(ApiTestHost Host, Guid ListId)> StartAsync()
    {
        Guid listId;

        // Started first, so what is written below is fresh by the host's own clock.
        var host = await ApiTestHost.StartAsync(db);

        await using (var context = db.NewContext())
        {
            // Pairings first: a list one names cannot go while it does.
            await context.DiscordListRoles.ExecuteDeleteAsync(Ct);
            await context.DiscordRolePairs.ExecuteDeleteAsync(Ct);
            await context.SavedLists.ExecuteDeleteAsync(Ct);
            await context.DiscordMembers.ExecuteDeleteAsync(Ct);
            await context.GroupMembers.ExecuteDeleteAsync(Ct);
            await context.DiscordAccountLinks.ExecuteDeleteAsync(Ct);
            await context.DiscordRoles.Where(r => r.GuildId == Guild).ExecuteDeleteAsync(Ct);
            await context.DiscordServers.Where(s => s.GuildId == Guild).ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;
            settings.DiscordListRolesOn = false;
            settings.DiscordLinkedRoleId = null;
            settings.DiscordEighteenPlusRoleId = null;

            var now = host.Clock.UtcNow;

            // The VRChat member list and the Discord member list both read just now.
            settings.MemberSweepCompletedAt = now;
            settings.AuditLogPolledAt = now;

            context.DiscordServers.Add(new DiscordServer
            {
                GuildId = Guild,
                Name = "The server",
                BotCanManageRoles = true,
                MembersReadAt = now,
                SeenThrough = now,
                RefreshedAt = now,
                UpdatedAt = now,
            });

            context.DiscordRoles.AddRange(
                Role(Regular, "Regular", (1L << 10) | (1L << 11), now),
                Role(Moderator, "Moderator", (1L << 10) | (1L << 2), now),
                Role(Unread, "Unread", null, now),
                Role(Paired, "Paired", 1L << 10, now),
                Role(Guild, "@everyone", 1L << 10, now, everyone: true));

            context.DiscordRolePairs.Add(new DiscordRolePair
            {
                VRChatRoleId = "grol_list_roles",
                DiscordRoleId = Paired,
                Decides = RoleSyncDecides.Nobody,
                CreatedAt = now,
                UpdatedAt = now,
            });

            var list = new SavedList { Id = Guid.CreateVersion7(), Name = "Regulars", Rules = InGroup, CreatedAt = now, UpdatedAt = now };
            context.SavedLists.Add(list);
            listId = list.Id;

            await context.SaveChangesAsync(Ct);
        }

        return (host, listId);
    }

    private static DiscordRole Role(string id, string name, long? permissions, DateTimeOffset now, bool everyone = false) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        BotCanAssign = !everyone,
        Everyone = everyone,
        FirstSeenAt = now,
        UpdatedAt = now,
    };

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    private static Task<HttpResponseMessage> AddAsync(ApiTestHost host, string cookie, Guid listId, string roleId)
        => host.SendJsonAsync(HttpMethod.Post, "/api/discord-list-roles", new { listId, discordRoleId = roleId }, cookie, Ct);

    [Fact]
    public async Task TheSwitchStartsOff()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/discord-list-roles", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("on").GetBoolean());
    }

    [Fact]
    public async Task ARoleWithAStaffPermissionIsRefused()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        var response = await AddAsync(host, cookie, listId, Moderator);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Ban Members", await ErrorOf(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Guild)]
    [InlineData(Unread)]
    [InlineData(Paired)]
    [InlineData("9999")]
    public async Task EveryoneUnreadPairedAndUnknownRolesAreRefused(string roleId)
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(host, cookie, listId, roleId)).StatusCode);
    }

    /// <summary>A list with no rules would give the role to everybody in the server.</summary>
    [Fact]
    public async Task AListThatLetsEverybodyInIsRefused()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        Guid empty;
        await using (var context = db.NewContext())
        {
            var list = new SavedList
            {
                Id = Guid.CreateVersion7(),
                Name = "Everybody",
                Rules = """{"kind":"allOf","rules":[]}""",
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            };
            context.SavedLists.Add(list);
            await context.SaveChangesAsync(Ct);
            empty = list.Id;
        }

        var response = await AddAsync(host, cookie, empty, Regular);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("That list lets everybody in. Give it rules first.", await ErrorOf(response));
    }

    /// <summary>
    /// A list that only names a list with no rules lets everybody in once that list is written out,
    /// and a list a paired list names is in use by that role too.
    /// </summary>
    [Fact]
    public async Task ListsNamedInsideAPairedListCount()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);
        var (_, listsOnly) = await host.SignedInAsync(SeeLists | ModbotPermissions.ManageLists, Ct);

        var empty = Guid.CreateVersion7();
        var outer = Guid.CreateVersion7();
        var naming = Guid.CreateVersion7();

        await using (var context = db.NewContext())
        {
            var now = host.Clock.UtcNow;
            context.SavedLists.AddRange(
                new SavedList { Id = empty, Name = "Nothing", Rules = """{"kind":"allOf","rules":[]}""", CreatedAt = now, UpdatedAt = now },
                new SavedList
                {
                    Id = outer,
                    Name = "In nothing",
                    Rules = $$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{empty}}"}]}""",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new SavedList
                {
                    Id = naming,
                    Name = "In regulars",
                    Rules = $$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{started.ListId}}"}]}""",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await context.SaveChangesAsync(Ct);
        }

        var refused = await AddAsync(host, cookie, outer, Regular);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("That list lets everybody in. Give it rules first.", await ErrorOf(refused));

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, naming, Regular)).StatusCode);

        var change = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/lists/{started.ListId}",
            new { name = "Regulars", rules = new { kind = "allOf", rules = new object[] { new { kind = "linkedAccounts" } } } },
            listsOnly,
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
    }

    /// <summary>Saving follows the preview, which names people in a list, so it needs what that needs.</summary>
    [Fact]
    public async Task SavingNeedsSeeMembersAndSeeProfiles()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var (_, setUpOnly) = await host.SignedInAsync(SetUp, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(host, setUpOnly, started.ListId, Regular)).StatusCode);
    }

    /// <summary>Two lists giving one role would undo each other every minute.</summary>
    [Fact]
    public async Task OneRoleCanBeGivenByOneListOnly()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, listId, Regular)).StatusCode);

        var again = await AddAsync(host, cookie, listId, Regular);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Another list already gives that role.", await ErrorOf(again));
    }

    /// <summary>A role a list gives cannot then be paired with a group role either.</summary>
    [Fact]
    public async Task ARoleAListGivesCannotBePairedWithAGroupRole()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, listId, Regular)).StatusCode);

        var pair = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/discord-sync/pairs",
            new { vrchatRoleId = "grol_other", discordRoleId = Regular, decides = "nobody", enabled = true },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, pair.StatusCode);
    }

    /// <summary>Lists design §6: the preview names people in a list, so it is not a way round seeing one.</summary>
    [Fact]
    public async Task ThePreviewNeedsSeeMembersAndSeeProfiles()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, setUpOnly) = await host.SignedInAsync(SetUp, Ct);
        var (_, both) = await host.SignedInAsync(SetUp | SeeLists, Ct);

        var body = new { listId, discordRoleId = Regular };

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/discord-list-roles/preview", body, setUpOnly, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/discord-list-roles/preview", body, both, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var plan = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("plans")[0];
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("problem").ValueKind);
    }

    /// <summary>
    /// Design §9: a list a role is given from cannot be deleted, and changing it needs Manage role
    /// and ban sync, since it changes who holds the role.
    /// </summary>
    [Fact]
    public async Task AListARoleIsGivenFromIsInUse()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);
        var (_, listsOnly) = await host.SignedInAsync(SeeLists | ModbotPermissions.ManageLists, Ct);

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, listId, Regular)).StatusCode);

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/lists", null, cookie, Ct), Ct);
        var roles = lists.GetProperty("lists")[0].GetProperty("usedBy").GetProperty("discordRoles");
        Assert.Equal("Regular", roles[0].GetString());

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/lists/{listId}", null, cookie, Ct)).StatusCode);

        var change = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/lists/{listId}",
            new { name = "Regulars", rules = new { kind = "allOf", rules = new object[] { new { kind = "linkedAccounts" } } } },
            listsOnly,
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
    }

    /// <summary>
    /// Design §5: Apply carries how many removals the person saw, and more than that is refused.
    /// </summary>
    [Fact]
    public async Task ApplyAllowsOnlyTheRemovalsSomebodySaw()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, listId, Regular)).StatusCode);

        // Four people Modbot gave the role to, none of them in the group.
        await using (var context = db.NewContext())
        {
            var pairing = await context.DiscordListRoles.SingleAsync(Ct);
            var now = host.Clock.UtcNow;

            foreach (var member in new[] { "9201", "9202", "9203", "9204" })
            {
                context.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = Guild,
                    UserId = member,
                    Username = "m" + member,
                    DisplayName = "Member " + member,
                    Roles = $"[\"{Regular}\"]",
                    FirstSeenAt = now,
                    UpdatedAt = now,
                });

                context.DiscordListRolesGiven.Add(new DiscordListRoleGiven { ListRoleId = pairing.Id, DiscordUserId = member, GivenAt = now });
            }

            await context.SaveChangesAsync(Ct);
        }

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/discord-list-roles", null, cookie, Ct), Ct);
        var id = view.GetProperty("roles")[0].GetProperty("id").GetGuid();

        var preview = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, "/api/discord-list-roles/preview", new { id }, cookie, Ct), Ct);
        var plan = preview.GetProperty("plans")[0];
        Assert.Equal(4, plan.GetProperty("taking").GetInt32());
        Assert.True(plan.GetProperty("stops").GetBoolean());

        var tooFew = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord-list-roles/{id}/apply", new { taking = 3, leaving = 0 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, tooFew.StatusCode);

        var applied = await host.SendJsonAsync(HttpMethod.Post, $"/api/discord-list-roles/{id}/apply", new { taking = 4, leaving = 0 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(4, (await ApiTestHost.BodyOf(applied, Ct)).GetProperty("roles")[0].GetProperty("removalsAllowed").GetInt32());
    }

    [Fact]
    public async Task ApplyNeedsRunRoleAndBanSync()
    {
        var started = await StartAsync();
        await using var host = started.Host;
        var listId = started.ListId;
        var (_, cookie) = await host.SignedInAsync(Everything, Ct);
        var (_, setUp) = await host.SignedInAsync(SetUp | SeeLists, Ct);

        await AddAsync(host, cookie, listId, Regular);
        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/discord-list-roles", null, cookie, Ct), Ct);
        var id = view.GetProperty("roles")[0].GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/discord-list-roles/{id}/apply", new { taking = 0 }, setUp, Ct)).StatusCode);
    }
}
