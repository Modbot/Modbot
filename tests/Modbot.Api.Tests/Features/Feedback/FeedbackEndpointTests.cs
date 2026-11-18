using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Feedback;

/// <summary>
/// The Feedback page's API: listing, opening and changing the status all need Review feedback, the
/// list runs newest first, and a status change is a fact that names the status but never the words.
/// Written, not run (Docker needed).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class FeedbackEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
            await context.Feedback.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db);
    }

    private async Task<Guid> AddAsync(string title, DateTimeOffset at, string kind = FeedbackKinds.Bug)
    {
        var item = new FeedbackItem
        {
            Id = Guid.CreateVersion7(),
            SenderUserId = Guid.NewGuid(),
            SenderName = "mina",
            Kind = kind,
            Title = title,
            Details = "The secret details of " + title,
            Status = FeedbackStatuses.New,
            CreatedAt = at,
        };

        await using var context = db.NewContext();
        context.Feedback.Add(item);
        await context.SaveChangesAsync(Ct);

        return item.Id;
    }

    [Fact]
    public async Task ListingOpeningAndMarkingNeedReviewFeedback()
    {
        await using var host = await StartAsync();
        var id = await AddAsync("Crash on start", DateTimeOffset.UnixEpoch);
        var (_, other) = await host.SignedInAsync(ModbotPermissions.SendFeedback | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/feedback", null, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Get, $"/api/feedback/{id}", null, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{id}/status", new { status = "done" }, other, Ct)).StatusCode);

        var (_, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewFeedback, Ct);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/feedback", null, reviewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheListRunsNewestFirst_CanBeNarrowedByStatus_AndAnOpenedItemHasItsWords()
    {
        await using var host = await StartAsync();
        var older = await AddAsync("Older", DateTimeOffset.UnixEpoch);
        var newer = await AddAsync("Newer", DateTimeOffset.UnixEpoch.AddDays(1), FeedbackKinds.Idea);
        var (_, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewFeedback, Ct);

        Assert.Equal(HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{older}/status", new { status = "reviewed" }, reviewer, Ct)).StatusCode);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/feedback", null, reviewer, Ct), Ct);
        var titles = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Newer", "Older"], titles);

        var reviewed = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/feedback?status=reviewed", null, reviewer, Ct), Ct);
        var only = Assert.Single(reviewed.GetProperty("items").EnumerateArray());
        Assert.Equal(older, only.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/feedback?status=nonsense", null, reviewer, Ct)).StatusCode);

        var full = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/feedback/{newer}", null, reviewer, Ct), Ct);
        Assert.Equal("The secret details of Newer", full.GetProperty("details").GetString());
        Assert.Equal("idea", full.GetProperty("kind").GetString());
        Assert.Equal("mina", full.GetProperty("senderName").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Get, $"/api/feedback/{Guid.NewGuid()}", null, reviewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task ChangingTheStatus_IsKept_NamesWhoDidIt_AndIsAFactWithoutTheWords()
    {
        await using var host = await StartAsync();
        var id = await AddAsync("Crash on start", DateTimeOffset.UnixEpoch);
        var (user, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewFeedback, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{id}/status", new { status = "done" }, reviewer, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal("done", body.GetProperty("status").GetString());
        Assert.Equal(user.Username, body.GetProperty("statusChangedBy").GetString());

        var fact = Assert.Single(await host.FactsAsync(FactType.FeedbackStatusChanged, id.ToString(), Ct));
        Assert.Contains("done", fact.Data);
        Assert.DoesNotContain("Crash on start", fact.Data);
        Assert.DoesNotContain("secret", fact.Data);

        // The same status again changes nothing and records nothing more.
        await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{id}/status", new { status = "done" }, reviewer, Ct);
        Assert.Single(await host.FactsAsync(FactType.FeedbackStatusChanged, id.ToString(), Ct));
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AStatusThatIsNotOneOfTheFour_IsRefused(string? status)
    {
        await using var host = await StartAsync();
        var id = await AddAsync("Crash on start", DateTimeOffset.UnixEpoch);
        var (_, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewFeedback, Ct);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{id}/status", new { status }, reviewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/feedback/{Guid.NewGuid()}/status", new { status = "done" }, reviewer, Ct)).StatusCode);
    }
}
