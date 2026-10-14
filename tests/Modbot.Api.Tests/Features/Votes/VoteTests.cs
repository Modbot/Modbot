using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Votes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Votes;

/// <summary>
/// Votes on the Modbot page: listing needs View posts, making and closing need Manage posts, the
/// choices (show counts, anonymous) are kept as sent, voters are shown only on a vote that is not
/// anonymous, a close survives Discord refusing the rewrite, and a vote that could not be posted is
/// not saved.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VoteTests
{
    private const string Guild = "700";
    private const string Channel = "900";
    private const string Regulars = "811";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public VoteTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVoteActions : IDiscordVoteActions
    {
        public VoteSiteOutcome PostResult { get; set; } = VoteSiteOutcome.Ok("5555");

        public VoteSiteOutcome ResultResult { get; set; } = VoteSiteOutcome.Ok();

        public List<Vote> Posted { get; } = [];

        public List<Guid> Closed { get; } = [];

        public Task<VoteSiteOutcome> PostAsync(Vote vote, CancellationToken ct = default)
        {
            Posted.Add(vote);
            return Task.FromResult(PostResult);
        }

        public Task<VoteSiteOutcome> RefreshAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
            => Task.FromResult(VoteSiteOutcome.Ok());

        public Task<VoteSiteOutcome> ShowResultAsync(Vote vote, IReadOnlyDictionary<Guid, int> counts, CancellationToken ct = default)
        {
            Closed.Add(vote.Id);
            return Task.FromResult(ResultResult);
        }

        public Task<VoteSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
            => Task.FromResult(VoteSiteOutcome.Ok());
    }

    private static async Task<(ReadSurfaceTestHost Host, FakeVoteActions Discord, string Cookie)> StartAsync(PostgresFixture db)
    {
        var discord = new FakeVoteActions();
        var host = await ReadSurfaceTestHost.StartAsync(db, configure: s => s.AddSingleton<IDiscordVoteActions>(discord));
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;

            var at = host.Clock.UtcNow;
            context.DiscordRoles.Add(new DiscordRole { RoleId = Regulars, GuildId = Guild, Name = "Regulars", FirstSeenAt = at, UpdatedAt = at });
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ManagePosts | ModbotPermissions.ViewPosts, Ct);
        return (host, discord, cookie);
    }

    private static object Body(
        string question = "Which world?",
        string[]? options = null,
        string? roleId = null,
        bool? showCounts = null,
        bool? anonymous = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["question"] = question,
            ["channelId"] = Channel,
            ["roleId"] = roleId,
            ["options"] = options ?? ["Forest", "Desert"],
        };

        if (showCounts is { } counts)
            body["showCounts"] = counts;

        if (anonymous is { } anon)
            body["anonymous"] = anon;

        return body;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.BadRequest)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("error").GetString() ?? string.Empty;
    }

    private static async Task PickAsync(ReadSurfaceTestHost host, VoteView vote, int option, string user, string? name)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        context.VotePicks.Add(new VotePick
        {
            VoteId = vote.Id,
            OptionId = vote.Options[option].Id,
            DiscordUserId = user,
            VotedAt = host.Clock.UtcNow,
        });

        if (name is not null)
        {
            context.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = user,
                Username = name.ToLowerInvariant(),
                DisplayName = name,
                FirstSeenAt = host.Clock.UtcNow,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AVote_IsPostedSavedAndListed_AnonymousWithNoCountsWhenTheChoicesAreLeftOut()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct));

        Assert.Equal("Which world?", created.Question);
        Assert.Equal("open", created.Status);
        Assert.False(created.ShowCounts);
        Assert.True(created.Anonymous);
        Assert.Equal(["Forest", "Desert"], created.Options.Select(o => o.Label));
        Assert.Single(discord.Posted);

        var list = await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct);
        Assert.Equal([created.Id], list.Votes.Select(v => v.Id));
        Assert.True(list.CanManage);
    }

    [Fact]
    public async Task TheChoices_ShowCountsAndAnonymous_AreKeptAsSent_AndSentOnToTheMessage()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync(
            "/api/votes", Body(showCounts: true, anonymous: false), cookie, Ct));

        Assert.True(created.ShowCounts);
        Assert.False(created.Anonymous);

        var posted = Assert.Single(discord.Posted);
        Assert.True(posted.ShowCounts);
        Assert.False(posted.Anonymous);

        var list = await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct);
        var listed = Assert.Single(list.Votes);
        Assert.True(listed.ShowCounts);
        Assert.False(listed.Anonymous);
    }

    [Fact]
    public async Task AVote_LimitedToARole_KeepsTheRole_AndARoleNotInTheServerIsRefused()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body(roleId: Regulars), cookie, Ct));
        Assert.Equal(Regulars, created.RoleId);

        var refused = await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(roleId: "99999"), cookie, Ct));
        Assert.Equal("That role is not in the Discord server.", refused);
        Assert.Single(discord.Posted);
    }

    [Fact]
    public async Task AVoteThatIsNotAllowed_IsRefusedWithWords_AndNothingIsPosted()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        Assert.Equal("Add at least two options.", await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(options: ["Only one"]), cookie, Ct)));
        Assert.Equal("Two options say the same thing.", await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(options: ["Forest", "forest"]), cookie, Ct)));
        Assert.Equal("Write the question.", await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(question: ""), cookie, Ct)));

        Assert.Empty(discord.Posted);
        Assert.Empty((await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct)).Votes);
    }

    [Fact]
    public async Task AVoteDiscordCouldNotPost_IsNotSaved_AndABotThatIsOfflineIsA503()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        discord.PostResult = VoteSiteOutcome.Failed("Missing permissions.");
        Assert.Equal("Missing permissions.", await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct)));

        discord.PostResult = VoteSiteOutcome.Offline;
        Assert.Equal(
            "The Discord bot is not connected.",
            await ErrorAsync(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct), HttpStatusCode.ServiceUnavailable));

        Assert.Empty((await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct)).Votes);
    }

    [Fact]
    public async Task ClosingAVote_StopsItAndRewritesTheMessage_AndClosingAgainChangesNothing()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct));
        await PickAsync(host, created, 1, "6001", null);

        var closed = await ReadAsync<VoteView>(await host.PostJsonAsync($"/api/votes/{created.Id}/close", null, cookie, Ct));

        Assert.Equal("closed", closed.Status);
        Assert.NotNull(closed.ClosedAt);
        Assert.Null(closed.MessageProblem);
        Assert.Equal(1, closed.Total);
        Assert.Equal([0, 1], closed.Options.Select(o => o.Count));
        Assert.Equal([created.Id], discord.Closed);

        var again = await ReadAsync<VoteView>(await host.PostJsonAsync($"/api/votes/{created.Id}/close", null, cookie, Ct));
        Assert.Equal("closed", again.Status);
        Assert.Equal(closed.ClosedAt, again.ClosedAt);
        Assert.Single(discord.Closed);
    }

    [Fact]
    public async Task ClosingAVote_WhenDiscordRefusesTheRewrite_StillClosesItAndSaysWhy()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct));
        discord.ResultResult = VoteSiteOutcome.Failed("The message is gone.");

        var closed = await ReadAsync<VoteView>(await host.PostJsonAsync($"/api/votes/{created.Id}/close", null, cookie, Ct));

        Assert.Equal("closed", closed.Status);
        Assert.Equal("The message is gone.", closed.MessageProblem);

        var listed = Assert.Single((await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct)).Votes);
        Assert.Equal("closed", listed.Status);
    }

    [Fact]
    public async Task ClosingAVoteThatIsNotThere_IsA404()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        var response = await host.PostJsonAsync($"/api/votes/{Guid.NewGuid()}/close", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Voters_AreShownOnlyOnAVoteThatIsNotAnonymous()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        var named = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body("Named", anonymous: false), cookie, Ct));
        var secret = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body("Secret"), cookie, Ct));

        await PickAsync(host, named, 0, "6002", "Bea");
        await PickAsync(host, named, 0, "6001", "Ada");
        await PickAsync(host, named, 1, "6003", null);
        await PickAsync(host, secret, 0, "6001", null);

        var list = (await host.GetJsonAsync<VotesResponse>("/api/votes", cookie, Ct)).Votes;
        var listedNamed = list.Single(v => v.Id == named.Id);
        var listedSecret = list.Single(v => v.Id == secret.Id);

        // Named, in the order of their names; somebody the server has no name for shows by id.
        Assert.Equal(["Ada", "Bea"], listedNamed.Options[0].Voters!.Select(v => v.Name));
        Assert.Equal(["6003"], listedNamed.Options[1].Voters!.Select(v => v.Name));
        Assert.Equal([2, 1], listedNamed.Options.Select(o => o.Count));

        // An anonymous vote still counts picks, but the answer has nobody's name or id.
        Assert.All(listedSecret.Options, o => Assert.Null(o.Voters));
        Assert.Equal(1, listedSecret.Total);
    }

    [Fact]
    public async Task AVoteIsListedToSomebodyWhoOnlyViewsPosts_ButCannotBeMadeOrClosedByThem()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<VoteView>(await host.PostJsonAsync("/api/votes", Body(), cookie, Ct));
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewPosts, Ct);

        var list = await host.GetJsonAsync<VotesResponse>("/api/votes", viewer, Ct);
        Assert.Equal([created.Id], list.Votes.Select(v => v.Id));
        Assert.False(list.CanManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/votes", Body(), viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/votes/{created.Id}/close", null, viewer, Ct)).StatusCode);

        Assert.Single(discord.Posted);
        Assert.Empty(discord.Closed);
    }

    [Fact]
    public async Task SomebodyWithoutViewPosts_IsTurnedAwayFromTheList()
    {
        var (host, _, _) = await StartAsync(_db);
        await using var _ = host;
        var other = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/votes", other, Ct)).StatusCode);
    }
}
