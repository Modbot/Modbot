using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Giveaways;

/// <summary>
/// The giveaways API (giveaways design §8): gated on ViewGiveaways and RunGiveaways, a fact for
/// every change, and the refusals §3.2 and §5.5 call for.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GiveawayEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Viewer = ModbotPermissions.ViewGiveaways;
    private const ModbotPermissions Runner = ModbotPermissions.ViewGiveaways | ModbotPermissions.RunGiveaways;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.Giveaways.ExecuteDeleteAsync(Ct);

            // And everybody a draw could pick. These tests make giveaways whose rules match every
            // member, so "nobody is in this giveaway" is only true while nobody is a member --
            // and another suite leaving one behind turned that refusal into a successful draw.
            // Nothing here adds a member, so clearing them costs these tests nothing.
            await context.GiveawayEntrants.ExecuteDeleteAsync(Ct);
            await context.DiscordAccountLinks.ExecuteDeleteAsync(Ct);
            await context.GroupMembers.ExecuteDeleteAsync(Ct);
            await context.DiscordMembers.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static object Body(
        ApiTestHost host,
        string name = "Autumn raffle",
        bool draft = false,
        object? rules = null,
        string weighting = "uniform",
        long? weightCap = null,
        string entryWay = "automatic",
        bool postToChannel = false,
        int winners = 1)
    {
        var opens = host.Clock.UtcNow;

        return new
        {
            name,
            prize = "A very silly hat",
            opensAt = opens,
            closesAt = opens.AddDays(7),
            drawAt = (DateTimeOffset?)null,
            winnerCount = winners,
            entryWay,
            emoji = "🎉",
            rules = rules ?? new { kind = "allOf", rules = Array.Empty<object>() },
            exclusions = new { staff = false, pastWinners = false, bannedMembers = false, people = Array.Empty<string>() },
            weighting,
            weightCap,
            postToChannel,
            channelId = postToChannel ? "222222222222222222" : null,
            draft,
        };
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    [Fact]
    public async Task SeeingGiveawaysNeedsSeeGiveaways()
    {
        await using var host = await StartAsync();
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/giveaways", null, nobody, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/giveaways", null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("canRun").GetBoolean());
    }

    [Fact]
    public async Task PlanningAGiveawayNeedsRunGiveaways_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", Body(host), viewer, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var id = await CreateAsync(host, runner, Body(host));

        Assert.Single(await host.FactsAsync(FactType.GiveawayCreated, id.ToString(), Ct));
    }

    /// <summary>
    /// §5.2: the promise is made when the giveaway is, so it is standing before anybody could have
    /// seen who would win under it.
    /// </summary>
    [Fact]
    public async Task ANewGiveawayAlreadyHasASeedPromise()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", Body(host), runner, Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        Assert.Equal(64, body.GetProperty("seedPromise").GetString()!.Length);
        Assert.Equal(0, body.GetProperty("drawCount").GetInt32());
    }

    [Fact]
    public async Task TheRulesComeBackAsWordsAsWellAsJson()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var rules = new
        {
            kind = "allOf",
            rules = new object[]
            {
                new { kind = "discordMemberDays", amount = 30 },
                new { kind = "inGroup" },
            },
        };

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", Body(host, rules: rules), runner, Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        var lines = body.GetProperty("ruleLines").EnumerateArray().Select(l => l.GetString()).ToList();

        Assert.Equal(["in Discord for 30 days or more", "in the group now"], lines);
    }

    [Fact]
    public async Task ARuleModbotDoesNotKnowIsRefusedByName()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var body = Body(host, rules: new { kind = "hasNiceHair" });
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", body, runner, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("'hasNiceHair' is not a rule Modbot knows.", await ErrorOf(response));
    }

    /// <summary>
    /// §5.5: a cap where everybody weighs the same is a control that does nothing, and a control
    /// that does nothing is a question somebody asks later.
    /// </summary>
    [Fact]
    public async Task ACapOnAUniformGiveawayIsRefusedRatherThanIgnored()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/giveaways", Body(host, weighting: "uniform", weightCap: 20), runner, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cap only means something", await ErrorOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGiveawayPeopleReactToHasToBePostedSomewhere()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/giveaways", Body(host, entryWay: "react", postToChannel: false), runner, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("posted to a channel", await ErrorOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItMustCloseAfterItOpens()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var opens = host.Clock.UtcNow;
        var body = new
        {
            name = "Backwards",
            prize = "",
            opensAt = opens,
            closesAt = opens.AddDays(-1),
            drawAt = (DateTimeOffset?)null,
            winnerCount = 1,
            entryWay = "automatic",
            emoji = "🎉",
            rules = new { kind = "allOf", rules = Array.Empty<object>() },
            exclusions = (object?)null,
            weighting = "uniform",
            weightCap = (long?)null,
            postToChannel = false,
            channelId = (string?)null,
            draft = false,
        };

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways", body, runner, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("It must close after it opens.", await ErrorOf(response));
    }

    [Fact]
    public async Task OpeningAndClosingAreFacts()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/close", null, runner, Ct)).StatusCode);

        Assert.Single(await host.FactsAsync(FactType.GiveawayClosed, id.ToString(), Ct));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/open", null, runner, Ct)).StatusCode);

        Assert.Single(await host.FactsAsync(FactType.GiveawayOpened, id.ToString(), Ct));
    }

    [Fact]
    public async Task CancellingIsAFactAndTakesItOutOfTheOpenList()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/cancel", null, runner, Ct);

        Assert.Single(await host.FactsAsync(FactType.GiveawayCancelled, id.ToString(), Ct));

        var body = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/giveaways/{id}", null, runner, Ct), Ct);

        Assert.Equal("cancelled", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ACancelledGiveawayCannotBeChanged()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/cancel", null, runner, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/giveaways/{id}", Body(host, "Renamed"), runner, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// §3.2: the draw copied the parameters, so editing them would not change the result -- but a
    /// page showing today's rules beside last week's winners reads as though the two go together.
    /// </summary>
    [Fact]
    public async Task ADrawnGiveawayCannotBeChanged()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        await using (var context = db.NewContext())
        {
            var giveaway = await context.Giveaways.SingleAsync(g => g.Id == id, Ct);
            giveaway.State = GiveawayStates.Drawn;
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/giveaways/{id}", Body(host, "Renamed"), runner, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("has been drawn", await ErrorOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DrawingAGiveawayNobodyIsInSaysSo()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/draw", null, runner, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Nobody is in this giveaway.", await ErrorOf(response));
    }

    [Fact]
    public async Task DrawingNeedsRunGiveaways()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/giveaways/{id}/draw", null, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task ThePreviewAnswersWithCountsAndAPage()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var body = new
        {
            rules = new { kind = "allOf", rules = Array.Empty<object>() },
            exclusions = (object?)null,
            weighting = "uniform",
            weightCap = (long?)null,
        };

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways/preview", body, runner, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answer = await ApiTestHost.BodyOf(response, Ct);

        Assert.Equal(JsonValueKind.Null, answer.GetProperty("unanswerable").ValueKind);
        Assert.False(answer.GetProperty("stopped").GetBoolean());
    }

    [Fact]
    public async Task ThePreviewNeedsRunGiveaways()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(Viewer, Ct);

        var body = new { rules = new { kind = "allOf", rules = Array.Empty<object>() } };

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/giveaways/preview", body, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheBuilderListsEveryRuleKindAndWeighting()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);

        var body = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/giveaways/builder", null, runner, Ct), Ct);

        var kinds = body.GetProperty("ruleKinds").EnumerateArray().Select(k => k.GetString()).ToList();

        Assert.Equal(GiveawayRuleKinds.Asking.Count, kinds.Count);
        Assert.Contains(GiveawayRuleKinds.InstanceHours, kinds);
        Assert.Contains(GiveawayRuleKinds.OneInstanceHours, kinds);

        var weightings = body.GetProperty("weightings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(GiveawayWeights.Uniform, weightings);
    }

    [Fact]
    public async Task DeletingHidesItAndLeavesTheFactBehind()
    {
        await using var host = await StartAsync();
        var (_, runner) = await host.SignedInAsync(Runner, Ct);
        var id = await CreateAsync(host, runner, Body(host));

        await host.SendJsonAsync(HttpMethod.Delete, $"/api/giveaways/{id}", null, runner, Ct);

        Assert.Single(await host.FactsAsync(FactType.GiveawayDeleted, id.ToString(), Ct));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Get, $"/api/giveaways/{id}", null, runner, Ct)).StatusCode);
    }
}
