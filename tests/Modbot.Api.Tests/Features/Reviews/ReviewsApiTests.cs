using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Reviews;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Reviews;

/// <summary>
/// The review surface: gated on <c>ReviewTickets</c>, lists what detection opened with its
/// evidence, and closes with a required note that becomes a fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReviewsApiTests
{
    private const string World = "wrld_a";

    private readonly PostgresFixture _db;

    public ReviewsApiTests(PostgresFixture db) => _db = db;

    /// <summary>One moderator, one person, three instances: the same-person check fires.</summary>
    private static async Task OpenOneReviewAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        var t = host.Clock.UtcNow;
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-5), actor: "usr_alice", actorName: "Alice", worldId: World, instanceId: "1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-3), actor: "usr_alice", actorName: "Alice", worldId: World, instanceId: "2"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_p", t.AddDays(-1), actor: "usr_alice", actorName: "Alice", worldId: World, instanceId: "3"), ct);
        await host.RunReviewsAsync(ct);
    }

    [Theory]
    [InlineData("/api/reviews")]
    [InlineData("/api/reviews/open-count")]
    public async Task AnUnauthenticatedCaller_Gets401(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutReviewTickets_Is403_EvenForAModeratorWhoCanReadEverythingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewProfile | ModbotPermissions.Kick, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/reviews", cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/reviews/open-count", cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.PostJsonAsync($"/api/reviews/{Guid.NewGuid()}/close", new { note = "x" }, cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task OpenReviews_AreListedWithTheirEvidenceAndNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await OpenOneReviewAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ReviewTickets, ct);
        var page = await host.GetJsonAsync<ReviewListResponse>("/api/reviews", cookie, ct);

        Assert.Equal(1, page.OpenCount);
        var review = Assert.Single(page.Reviews);

        Assert.Equal("usr_alice", review.Moderator.Id);
        Assert.Equal("Alice", review.Moderator.Name);
        Assert.Equal(ReviewSignal.SamePerson, review.Signal);
        Assert.Equal("Keeps acting on one person", review.SignalLabel);
        Assert.Equal("usr_p", review.AboutPerson?.Id);
        Assert.Equal("Open", review.State);
        Assert.Contains("3 times", review.Summary);
        Assert.Equal(3, review.Evidence.GetProperty("actions").GetInt32());
        Assert.Equal(3, review.Evidence.GetProperty("factIds").GetArrayLength());

        Assert.NotNull(page.LastRunAt);

        var count = await host.GetJsonAsync<OpenReviewCount>("/api/reviews/open-count", cookie, ct);
        Assert.Equal(1, count.Open);
    }

    [Fact]
    public async Task Closing_NeedsANote_RecordsWho_AndWritesAFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await OpenOneReviewAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ReviewTickets, ct);
        var open = Assert.Single((await host.GetJsonAsync<ReviewListResponse>("/api/reviews", cookie, ct)).Reviews);

        // No note, no close.
        var refused = await host.PostJsonAsync($"/api/reviews/{open.Id}/close", new { note = "   " }, cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var closed = await host.PostJsonAsync($"/api/reviews/{open.Id}/close", new { note = "Asked Alice; a crasher kept coming back." }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var view = System.Text.Json.JsonSerializer.Deserialize<ReviewView>(
            await closed.Content.ReadAsStringAsync(ct),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("Closed", view.State);
        Assert.Equal("Asked Alice; a crasher kept coming back.", view.Note);
        Assert.Equal(host.Clock.UtcNow, view.ClosedAt);
        Assert.False(string.IsNullOrEmpty(view.ClosedByUsername));

        // Gone from the open list, and the count.
        Assert.Empty((await host.GetJsonAsync<ReviewListResponse>("/api/reviews", cookie, ct)).Reviews);
        Assert.Equal(0, (await host.GetJsonAsync<OpenReviewCount>("/api/reviews/open-count", cookie, ct)).Open);
        Assert.Single((await host.GetJsonAsync<ReviewListResponse>("/api/reviews?state=closed", cookie, ct)).Reviews);

        // Closing twice is refused.
        var again = await host.PostJsonAsync($"/api/reviews/{open.Id}/close", new { note = "again" }, cookie, ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The fact: about the moderator, by the account that closed it, carrying the note.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.ReviewClosed, ct);

        Assert.Equal("usr_alice", fact.SubjectId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(FactSource.Modbot, fact.Source);
        Assert.Contains("a crasher kept coming back", fact.Data);
        Assert.Contains(open.Id.ToString(), fact.Data);

        // And the opening was a fact too.
        Assert.Equal(1, await db.Events.AsNoTracking().CountAsync(e => e.Type == FactType.ReviewOpened, ct));

        // Closed: detection does not reopen it on the same facts.
        var rerun = await host.RunReviewsAsync(ct);
        Assert.Equal(0, rerun.ReviewsOpened);
    }

    [Fact]
    public async Task ClosingAnUnknownReview_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ReviewTickets, ct);
        var response = await host.PostJsonAsync($"/api/reviews/{Guid.NewGuid()}/close", new { note = "x" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ABadStateFilter_Is400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ReviewTickets, ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/reviews?state=weird", cookie, ct)).StatusCode);
    }
}
