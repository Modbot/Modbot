using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Lists;

/// <summary>
/// The saved lists API (lists design §6, §7): seeing a list needs See members and See profiles,
/// changing one needs Manage lists and, for a list something uses, that thing's own permission;
/// the export is a fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ListEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Viewer = ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;
    private const ModbotPermissions Maker = Viewer | ModbotPermissions.ManageLists;

    private const string Person = "usr_list_person";

    /// <summary>When the test member joined. Fixed: nothing here is about how long ago.</summary>
    private static readonly DateTimeOffset Joined = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            // Lists are read across the whole table, and who is in one across every member, so
            // another suite's leftovers would change the answers here.
            await context.SavedLists.ExecuteDeleteAsync(Ct);
            await context.Giveaways.ExecuteDeleteAsync(Ct);
            await context.DiscordAccountLinks.ExecuteDeleteAsync(Ct);
            await context.GroupMembers.ExecuteDeleteAsync(Ct);
            await context.DiscordMembers.ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.GroupAutoInviteRules = GiveawayRules.Store(GiveawayRule.Everyone);
            await context.SaveChangesAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static object Body(string name = "Regulars", object? rules = null)
        => new { name, rules = rules ?? new { kind = "allOf", rules = new object[] { new { kind = "inGroup" } } } };

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/lists", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    private async Task AddMemberAsync(string userId, string name)
    {
        await using var context = db.NewContext();

        if (!await context.VRChatUsers.AnyAsync(u => u.UserId == userId, Ct))
        {
            context.VRChatUsers.Add(new VRChatUser
            {
                UserId = userId,
                DisplayName = name,
                FirstSeenAt = Joined,
                LastSeenAt = Joined,
            });
        }
        else
        {
            var user = await context.VRChatUsers.SingleAsync(u => u.UserId == userId, Ct);
            user.DisplayName = name;
        }

        context.GroupMembers.Add(new GroupMember
        {
            GroupId = "grp_lists",
            UserId = userId,
            Roles = "[]",
            JoinedAt = Joined,
            FirstSeenAt = Joined,
            LastSeenAt = Joined,
        });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// A list's rules can ask about bans and 18+ verification, so seeing one, who is in it, or an
    /// export of it needs See profiles as well as See members (decided 2026-10-01).
    /// </summary>
    [Fact]
    public async Task SeeingListsNeedsSeeMembersAndSeeProfiles()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);
        var (_, membersOnly) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageLists, Ct);
        var (_, profilesOnly) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);

        var id = await CreateAsync(host, maker, Body());

        foreach (var cookie in new[] { membersOnly, profilesOnly })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await host.SendJsonAsync(HttpMethod.Get, "/api/lists", null, cookie, Ct)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await host.SendJsonAsync(HttpMethod.Get, $"/api/lists/{id}/people", null, cookie, Ct)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await host.SendJsonAsync(HttpMethod.Post, $"/api/lists/{id}/export", new { format = "csv" }, cookie, Ct)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/lists/preview", new { rules = new { kind = "allOf" } }, membersOnly, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/lists", null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("canManage").GetBoolean());
    }

    [Fact]
    public async Task MakingAListNeedsManageLists_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/lists", Body(), viewer, Ct)).StatusCode);

        var id = await CreateAsync(host, maker, Body());

        Assert.Single(await host.FactsAsync(FactType.ListCreated, id.ToString(), Ct));
    }

    /// <summary>One pass of expanding is all a tree ever takes, and no list can name itself.</summary>
    [Fact]
    public async Task AListCannotUseAnotherList()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        var first = await CreateAsync(host, maker, Body());

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/lists",
            Body("Inside", new { kind = "allOf", rules = new object[] { new { kind = "inList", id = first.ToString() } } }),
            maker,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A list cannot use another list.", await ErrorOf(response));
    }

    [Fact]
    public async Task TwoListsCannotShareAName()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        await CreateAsync(host, maker, Body("Regulars"));

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/lists", Body("regulars"), maker, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("There is already a list with that name.", await ErrorOf(response));
    }

    /// <summary>
    /// Lists design §6: a list a giveaway names decides who it reaches, so changing it takes Run
    /// giveaways too, and deleting it is refused while the giveaway is still being run.
    /// </summary>
    [Fact]
    public async Task AListAGiveawayUsesNeedsRunGiveawaysToChange_AndCannotBeDeleted()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);
        var (_, runner) = await host.SignedInAsync(Maker | ModbotPermissions.RunGiveaways, Ct);

        var id = await CreateAsync(host, maker, Body());

        await using (var context = db.NewContext())
        {
            context.Giveaways.Add(new Giveaway
            {
                Id = Guid.CreateVersion7(),
                Name = "Autumn raffle",
                OpensAt = host.Clock.UtcNow,
                ClosesAt = host.Clock.UtcNow.AddDays(7),
                WinnerCount = 1,
                EntryWay = GiveawayEntryWays.Automatic,
                Rules = GiveawayRules.Store(new GiveawayRule
                {
                    Rules = [new GiveawayRule { Kind = GiveawayRuleKinds.InList, Id = id.ToString("D") }],
                }),
                State = GiveawayStates.Open,
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });

            await context.SaveChangesAsync(Ct);
        }

        var refused = await host.SendJsonAsync(HttpMethod.Put, $"/api/lists/{id}", Body("Regulars, renamed"), maker, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Autumn raffle", await ErrorOf(refused));

        var changed = await host.SendJsonAsync(HttpMethod.Put, $"/api/lists/{id}", Body("Regulars, renamed"), runner, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.ListChanged, id.ToString(), Ct));

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/lists/{id}", null, runner, Ct);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("The giveaway “Autumn raffle” uses this list.", await ErrorOf(deleted));

        var listed = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/lists", null, maker, Ct), Ct);
        var usedBy = listed.GetProperty("lists")[0].GetProperty("usedBy");
        Assert.Equal("Autumn raffle", usedBy.GetProperty("giveaways")[0].GetString());
    }

    [Fact]
    public async Task AListNothingUsesCanBeDeleted_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        var id = await CreateAsync(host, maker, Body());

        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/lists/{id}", null, maker, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.ListDeleted, id.ToString(), Ct));

        var listed = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/lists", null, maker, Ct), Ct);
        Assert.Equal(0, listed.GetProperty("lists").GetArrayLength());
    }

    [Fact]
    public async Task WhoIsInAListIsWorkedOutWhenAsked()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        var id = await CreateAsync(host, maker, Body());
        await AddMemberAsync(Person, "Ada");

        var body = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/lists/{id}/people", null, maker, Ct), Ct);

        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal("Ada", body.GetProperty("people")[0].GetProperty("name").GetString());
        Assert.True(body.GetProperty("people")[0].GetProperty("inGroup").GetBoolean());
    }

    /// <summary>
    /// Lists design §7: an export is recorded, names its format, count and columns but not the
    /// people, and cannot run a formula in whatever spreadsheet opens it.
    /// </summary>
    [Fact]
    public async Task AnExportIsAFact_AndCannotRunAFormula()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        var id = await CreateAsync(host, maker, Body());
        await AddMemberAsync(Person, "=HYPERLINK(\"x\")");

        var plain = await host.SendJsonAsync(HttpMethod.Post, $"/api/lists/{id}/export", new { format = "csv" }, maker, Ct);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("text/csv", plain.Content.Headers.ContentType?.MediaType);

        var text = await plain.Content.ReadAsStringAsync(Ct);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "name,vrchatUserId,discordUserId,linked,inGroup,joinedGroupAt,inDiscord,joinedDiscordAt,"
            + "trustRank,is18PlusVerified,vrchatAccountCreated,firstSeenAt",
            lines[0]);
        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",usr_list_person,", lines[1]);

        var fact = Assert.Single(await host.FactsAsync(FactType.ListExported, id.ToString(), Ct));
        using (var data = JsonDocument.Parse(fact.Data))
        {
            Assert.Equal("csv", data.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, data.RootElement.GetProperty("count").GetInt32());
            Assert.DoesNotContain("usr_list_person", fact.Data, StringComparison.Ordinal);
        }

        var asJson = await host.SendJsonAsync(HttpMethod.Post, $"/api/lists/{id}/export", new { format = "json" }, maker, Ct);
        Assert.Equal(HttpStatusCode.OK, asJson.StatusCode);

        var json = await ApiTestHost.BodyOf(asJson, Ct);
        var row = json.GetProperty("people")[0];
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.True(row.TryGetProperty("trustRank", out _));
        Assert.True(row.GetProperty("inGroup").GetBoolean());
    }

    [Fact]
    public async Task AnExportNeedsAFormatModbotKnows()
    {
        await using var host = await StartAsync();
        var (_, maker) = await host.SignedInAsync(Maker, Ct);

        var id = await CreateAsync(host, maker, Body());

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/lists/{id}/export", new { format = "xlsx" }, maker, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A giveaway or auto-invites cannot be saved naming a list that is not there.</summary>
    [Fact]
    public async Task AutoInvitesCannotNameAListThatDoesNotExist()
    {
        await using var host = await StartAsync();
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/auto-invites",
            new
            {
                enabled = false,
                minutesInInstance = 10,
                inviteAgainAfterDays = 30,
                rules = new { kind = "allOf", rules = new object[] { new { kind = "inList", id = Guid.NewGuid().ToString() } } },
            },
            admin,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("That list does not exist any more.", await ErrorOf(response));
    }
}
