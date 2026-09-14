using System.Net;
using Modbot.Api.Features.Reviews;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Reviews;

/// <summary>
/// The repeat-offender surface: the list of people acted on more than once, and the History block
/// for one person's pane. Gated on <c>ViewProfile</c> -- it is a person's history.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RepeatOffendersApiTests
{
    private readonly PostgresFixture _db;

    public RepeatOffendersApiTests(PostgresFixture db) => _db = db;

    [Theory]
    [InlineData("/api/repeat-offenders")]
    [InlineData("/api/repeat-offenders/one?id=usr_p")]
    public async Task AnUnauthenticatedCaller_Gets401(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(path, ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/repeat-offenders")]
    [InlineData("/api/repeat-offenders/one?id=usr_p")]
    public async Task WithoutViewProfile_Is403(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(path, cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task TheList_HasPeopleActedOnMoreThanOnce_MostRecentFirst_WithNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow;

        // usr_twice: two kicks by two moderators, the later one by Bob. usr_once: one kick.
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_twice", t.AddDays(-10), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_twice", t.AddDays(-2), actor: "usr_bob", actorName: "Bob"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_once", t.AddDays(-1), actor: "usr_alice", actorName: "Alice"), ct);

        // usr_three: three actions in 30 days -- a repeat offender; most recent of all.
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_three", t.AddDays(-6), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_three", t.AddDays(-4), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_three", t.AddHours(-1), actor: "usr_bob", actorName: "Bob"), ct);

        await host.RunReviewsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var page = await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders", cookie, ct);

        Assert.Equal(2, page.Total);
        Assert.Equal(["usr_three", "usr_twice"], page.People.Select(p => p.Who.Id).ToArray());
        Assert.NotNull(page.LastRunAt);
        Assert.Equal("Repeat: 3 or more actions in the last 30 days.", page.Rule);

        var three = page.People[0];
        Assert.Equal(3, three.Actions);
        Assert.Equal(1, three.Bans);
        Assert.Equal(1, three.InstanceKicks);
        Assert.Equal(1, three.Warns);
        Assert.Equal(2, three.Moderators);
        Assert.Equal(RepeatOffenderStatus.Repeat, three.Status);
        Assert.Equal("Banned", three.LastActionLabel);
        Assert.Equal("usr_bob", three.LastBy?.Id);
        Assert.Equal("Bob", three.LastBy?.Name);

        var twice = page.People[1];
        Assert.Equal(RepeatOffenderStatus.MoreThanOnce, twice.Status);
        Assert.Equal(t.AddDays(-2), twice.LastActionAt);

        // Filtering to repeat offenders only.
        var repeats = await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders?status=repeat", cookie, ct);
        Assert.Equal("usr_three", Assert.Single(repeats.People).Who.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/repeat-offenders?status=nope", cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task OnePerson_HasAHistoryBlock_OrIsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow;
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-3), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_p", t.AddDays(-2), actor: "usr_alice", actorName: "Alice"), ct);
        await host.RunReviewsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var known = await host.GetJsonAsync<SubjectHistory>("/api/repeat-offenders/one?id=usr_p", cookie, ct);
        Assert.True(known.Known);
        Assert.Equal(1, known.Counts?.Actions);
        Assert.Equal(1, known.Counts?.Unbans);
        Assert.Equal(RepeatOffenderStatus.Once, known.Counts?.Status);
        Assert.Equal("Alice", known.Counts?.LastBy?.Name);

        var unknown = await host.GetJsonAsync<SubjectHistory>("/api/repeat-offenders/one?id=usr_nobody", cookie, ct);
        Assert.False(unknown.Known);
        Assert.Null(unknown.Counts);
        Assert.False(string.IsNullOrWhiteSpace(unknown.Rule));

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/repeat-offenders/one?id=", cookie, ct)).StatusCode);
    }
}
