using System.Net;
using System.Net.Http.Json;
using Modbot.Analytics.Reviews;
using Modbot.Api.Features.Reviews;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Moderation → Repeat offenders: the threshold and the kinds of action that count,
/// both of which rebuild the standings when they change.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RepeatOffenderSettingsTests
{
    private const string Path = "/api/settings/repeat-offenders";

    private readonly PostgresFixture _db;

    public RepeatOffenderSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutManageSettings_ItIsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, cookie, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PutJsonAsync(Path, new { threshold = 4, types = new[] { FactType.MemberBanned } }, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheDefaults_AreEveryKindAndThreeInThirtyDays()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var view = await host.GetJsonAsync<RepeatOffenderRulesView>(Path, cookie, Ct);

        Assert.Equal(3, view.Threshold);
        Assert.All(view.Types, t => Assert.True(t.Counts));
        Assert.Contains(view.Types, t => t.Value == FactType.GroupInstanceKick && t.Label == "Kicked from an instance");
    }

    [Fact]
    public async Task ChangingTheThreshold_RebuildsTheStandings()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var t = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-1), actor: "usr_alice"), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_p", t.AddDays(-2), actor: "usr_alice"), Ct);
        await host.WriteFactAsync(AuditFact(FactType.JoinRequestRejected, "usr_p", t.AddDays(-3), actor: "usr_bob"), Ct);

        await host.RunReviewsAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.ViewProfile, Ct);

        var before = await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders", cookie, Ct);
        Assert.Equal(RepeatOffenderStatus.Repeat, Assert.Single(before.People).Status);

        var response = await host.PutJsonAsync(
            Path,
            new { threshold = 5, types = ActionsOnPeople.Types },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<RepeatOffenderRulesView>(Ct);
        Assert.Equal(5, saved!.Threshold);

        // Rebuilt by the save, not left until the next scheduled run: an operator who has just
        // raised the bar must not be shown the rule they replaced.
        var after = await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders", cookie, Ct);
        Assert.Equal(RepeatOffenderStatus.MoreThanOnce, Assert.Single(after.People).Status);
        Assert.Equal("Repeat: 5 or more actions in the last 30 days.", after.Rule);
    }

    [Fact]
    public async Task ExcludingAKind_RebuildsTheStandingsWithoutIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var t = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-1), actor: "usr_alice"), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_p", t.AddDays(-2), actor: "usr_bob"), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_p", t.AddDays(-3), actor: "usr_bob"), Ct);

        await host.RunReviewsAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(
            RepeatOffenderStatus.Repeat,
            Assert.Single((await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders", cookie, Ct)).People).Status);

        var response = await host.PutJsonAsync(
            Path,
            new { threshold = 3, types = new[] { FactType.MemberBanned, FactType.MemberKicked } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Nobody is left on the list: every action against this person was a kind that no longer
        // counts, so they are not somebody who has been acted on more than once.
        var after = await host.GetJsonAsync<RepeatOffenderListResponse>("/api/repeat-offenders", cookie, Ct);
        Assert.Empty(after.People);

        var view = await host.GetJsonAsync<RepeatOffenderRulesView>(Path, cookie, Ct);
        Assert.Equal(
            new[] { FactType.MemberBanned, FactType.MemberKicked }.Order().ToArray(),
            view.Types.Where(x => x.Counts).Select(x => x.Value).Order().ToArray());
    }

    [Fact]
    public async Task NoKinds_OrAKindThatIsNotAnAction_IsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PutJsonAsync(Path, new { threshold = 3, types = Array.Empty<string>() }, cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PutJsonAsync(Path, new { threshold = 3, types = new[] { FactType.MemberJoined } }, cookie, Ct)).StatusCode);
    }
}
