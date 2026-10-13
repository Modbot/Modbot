using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Spotlight;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Spotlight;

/// <summary>
/// The spotlight on the Marketing page (spotlight design §3): a staff-mode draft waits for a member,
/// staff pick one who opted in, picking fills the draft with that member's card and only once, and
/// the Marketing permissions decide who sees it and who picks.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SpotlightEndpointsTests
{
    private const string Guild = "424242";
    private const string Path = "/api/spotlight";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public SpotlightEndpointsTests(PostgresFixture db) => _db = db;

    /// <summary>Ada and Bo opted in; Cy did not. The spotlight is on in staff mode, with no draft yet.</summary>
    private async Task<(ApiTestHost Host, string Cookie)> StartAsync(ModbotPermissions permissions = ModbotPermissions.ManagePosts | ModbotPermissions.ViewPosts)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using (var context = _db.NewContext())
        {
            await context.SpotlightWeeks.ExecuteDeleteAsync(Ct);
            await context.Posts.Where(p => p.Kind == PostKinds.Spotlight).ExecuteDeleteAsync(Ct);
            await context.MemberProfiles.ExecuteDeleteAsync(Ct);
            await context.ProfileFields.ExecuteDeleteAsync(Ct);
            await context.DiscordMembers.Where(m => m.GuildId == Guild).ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.SpotlightOn = true;
            settings.SpotlightMode = SpotlightModes.Staff;
            settings.SpotlightPlaces = "{}";

            context.DiscordMembers.AddRange(Member("1", "Ada"), Member("2", "Bo"), Member("3", "Cy"));
            context.MemberProfiles.AddRange(
                new MemberProfile { DiscordUserId = "1", Spotlight = true, UpdatedAt = Start },
                new MemberProfile { DiscordUserId = "2", Spotlight = true, UpdatedAt = Start });

            await context.SaveChangesAsync(Ct);
        }

        var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(permissions, Ct);
        return (host, cookie);
    }

    private static DiscordMember Member(string id, string name) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = name.ToLowerInvariant(),
        DisplayName = name,
        FirstSeenAt = Start.AddDays(-30),
        UpdatedAt = Start,
    };

    /// <summary>The pass makes the week's draft, as it does once a minute.</summary>
    private async Task MakeDraftAsync()
    {
        var clock = new FakeClock(Start);
        await using var context = _db.NewContext();
        var pass = new SpotlightPass(
            context,
            clock,
            new AccountFacts(new FactWriter(context, clock), new EventPartitionMaintainer(context, clock), clock));

        Assert.True(await pass.RunOnceAsync(Ct));
    }

    private async Task<Guid> WeekIdAsync()
    {
        await using var context = _db.NewContext();
        return await context.SpotlightWeeks.AsNoTracking().Select(w => w.Id).SingleAsync(Ct);
    }

    [Fact]
    public async Task NothingWaitsUntilADraftWithNoMemberIsMade()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pending").ValueKind);
        Assert.Empty(body.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task ADraftWithNoMemberWaits_WithTheMembersWhoOptedInByName()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;
        await MakeDraftAsync();

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);

        Assert.Equal(await WeekIdAsync(), body.GetProperty("pending").GetProperty("id").GetGuid());
        Assert.Equal("2026-10-12", body.GetProperty("pending").GetProperty("week").GetString());
        Assert.Equal(["Ada", "Bo"], body.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task PickingAMemberFillsTheDraftWithTheirCard_AndOnlyOnce()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;
        await MakeDraftAsync();
        var id = await WeekIdAsync();

        var picked = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = "2" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

        await using (var context = _db.NewContext())
        {
            var week = await context.SpotlightWeeks.AsNoTracking().SingleAsync(Ct);
            Assert.Equal("2", week.DiscordUserId);
            Assert.NotNull(week.PickedAt);

            var post = await context.Posts.AsNoTracking().SingleAsync(p => p.Id == week.PostId, Ct);
            Assert.Equal("Member spotlight: Bo", post.Title);
            Assert.Equal(PostStatuses.Draft, post.Status);
        }

        // Nothing waits any more.
        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("pending").ValueKind);

        // A second pick of the same week is refused and changes nothing.
        var again = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = "1" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SpotlightEndpoints.AlreadyPicked, (await ApiTestHost.BodyOf(again, Ct)).GetProperty("error").GetString());

        await using var read = _db.NewContext();
        Assert.Equal("2", (await read.SpotlightWeeks.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task PickingKeepsTheNoteStaffAlreadyTyped_AndTheirTitleAndPicture()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;
        await MakeDraftAsync();
        var id = await WeekIdAsync();

        var picture = new CalendarCoverPicture
        {
            Id = Guid.CreateVersion7(),
            Bytes = [1, 2, 3],
            ContentType = "image/png",
            CreatedAt = Start,
        };

        await using (var context = _db.NewContext())
        {
            var hobby = new ProfileField { Name = "Hobby", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.Members, SortOrder = 0, CreatedAt = Start, UpdatedAt = Start };
            context.ProfileFields.Add(hobby);
            context.CalendarCoverPictures.Add(picture);
            await context.SaveChangesAsync(Ct);

            await Modbot.Core.Profiles.MemberProfiles.SetAnswersAsync(
                context, "2", new Dictionary<Guid, string?> { [hobby.Id] = "Pottery" }, Start, Ct);

            await context.Posts.Where(p => p.Kind == PostKinds.Spotlight).ExecuteUpdateAsync(
                u => u.SetProperty(p => p.Text, "Welcome Bo!").SetProperty(p => p.Title, "Our star").SetProperty(p => p.PictureId, picture.Id), Ct);
        }

        var picked = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = "2" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

        await using var read = _db.NewContext();
        var post = await read.Posts.AsNoTracking().SingleAsync(p => p.Kind == PostKinds.Spotlight, Ct);
        Assert.Equal("Hobby: Pottery\n\nWelcome Bo!", post.Text);
        Assert.Equal("Our star", post.Title);
        Assert.Equal(picture.Id, post.PictureId);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("999")]
    [InlineData("")]
    public async Task AMemberWhoDidNotOptInCannotBePicked(string member)
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;
        await MakeDraftAsync();
        var id = await WeekIdAsync();

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = member }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SpotlightEndpoints.NotOptedIn, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        await using var context = _db.NewContext();
        Assert.Null((await context.SpotlightWeeks.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task ASpotlightThatDoesNotExistIsNotFound()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{Guid.NewGuid()}/member", new { discordUserId = "1" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ACancelledDraftCannotBeFilled()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;
        await MakeDraftAsync();
        var id = await WeekIdAsync();

        await using (var context = _db.NewContext())
        {
            await context.Posts.Where(p => p.Kind == PostKinds.Spotlight)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PostStatuses.Cancelled), Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = "1" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var read = _db.NewContext();
        Assert.Null((await read.SpotlightWeeks.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task SeeingNeedsViewPosts_AndPickingNeedsManagePosts()
    {
        var (host, viewer) = await StartAsync(ModbotPermissions.ViewPosts);
        await using var running = host;
        await MakeDraftAsync();
        var id = await WeekIdAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Path, null, viewer, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/{id}/member", new { discordUserId = "1" }, viewer, Ct)).StatusCode);

        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, nobody, Ct)).StatusCode);
    }
}
