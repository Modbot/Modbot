using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Supporters;
using Modbot.Core.Time;
using Modbot.Discord.Tests.Fakes;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Supporters;

/// <summary>
/// The Patreon catch-up and Discord lookup against a scripted Patreon: members read page by page and fed
/// through the intake, a Discord account matched from the user's connected apps (or left to staff when
/// another supporter has it), the access token replaced from the refresh token, one request a second, and
/// a 429 that stops everything and is not retried.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PatreonCatchUpTests
{
    private static readonly DateTimeOffset Charged = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public PatreonCatchUpTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The scripted Patreon ─────────────────────────────────────────────────────────────

    private sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? Body);

    private sealed class Patreon(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public int Calls(string part) => Requests.Count(r => r.Url.Contains(part, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), request.Headers.Authorization?.ToString(), body));

            return answer(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string MemberJson(string id, string status, string tier, string? charge = "2026-01-01T00:00:00.000+00:00")
        => """
            {"id":"@ID@","type":"member","attributes":{"patron_status":"@STATUS@","full_name":"Patron @ID@","last_charge_date":@CHARGE@,"last_charge_status":"Paid"},
             "relationships":{"currently_entitled_tiers":{"data":[{"id":"@TIER@","type":"tier"}]},"user":{"data":{"id":"u-@ID@","type":"user"}}}}
            """
            .Replace("@ID@", id, StringComparison.Ordinal)
            .Replace("@STATUS@", status, StringComparison.Ordinal)
            .Replace("@TIER@", tier, StringComparison.Ordinal)
            .Replace("@CHARGE@", charge is null ? "null" : "\"" + charge + "\"", StringComparison.Ordinal);

    private static string UserJson(string memberId, string? discord)
        => """{"id":"u-@ID@","type":"user","attributes":{"social_connections":@CONNECTIONS@}}"""
            .Replace("@ID@", memberId, StringComparison.Ordinal)
            .Replace("@CONNECTIONS@", discord is null ? """{"discord":null,"twitter":null}""" : """{"discord":{"user_id":"@D@"}}""".Replace("@D@", discord, StringComparison.Ordinal), StringComparison.Ordinal);

    private const string Tiers =
        """{"id":"silver","type":"tier","attributes":{"title":"Silver","amount_cents":500}},{"id":"gold","type":"tier","attributes":{"title":"Gold","amount_cents":1500}}""";

    private static string PageJson(string[] members, string[] users, string? next)
        => """{"data":[@MEMBERS@],"included":[@TIERS@,@USERS@],"meta":{"pagination":{"cursors":{"next":@NEXT@},"total":3}}}"""
            .Replace("@MEMBERS@", string.Join(",", members), StringComparison.Ordinal)
            .Replace("@TIERS@", Tiers, StringComparison.Ordinal)
            .Replace(",@USERS@", users.Length == 0 ? "" : "," + string.Join(",", users), StringComparison.Ordinal)
            .Replace("@NEXT@", next is null ? "null" : "\"" + next + "\"", StringComparison.Ordinal);

    private const string Campaigns = """{"data":[{"id":"camp-1","type":"campaign"}]}""";

    // ── Set-up ───────────────────────────────────────────────────────────────────────────

    private static async Task ConfigureAsync(TestServices services, Action<Settings>? more = null)
    {
        await using var db = services.Database.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        var protector = new FakeSecretProtector();

        settings.PatreonOn = true;
        settings.PatreonClientIdEncrypted = protector.Protect("client-id");
        settings.PatreonClientSecretEncrypted = protector.Protect("client-secret");
        settings.PatreonAccessTokenEncrypted = protector.Protect("access-1");
        settings.PatreonRefreshTokenEncrypted = protector.Protect("refresh-1");
        settings.PatreonTokenExpiresAt = services.Clock.UtcNow.AddDays(20);
        more?.Invoke(settings);

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<PatreonPassResult> RunAsync(TestServices services, Patreon patreon, List<TimeSpan>? delays = null)
    {
        using var scope = services.Scope();
        var provider = scope.ServiceProvider;

        var catchUp = new PatreonCatchUp(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<IModbotClock>(),
            provider.GetRequiredService<ISecretProtector>(),
            new PatreonClient(new Factory(patreon)),
            provider.GetRequiredService<SupporterIntake>(),
            new SupporterSignal(),
            (gap, _) =>
            {
                delays?.Add(gap);
                return Task.CompletedTask;
            });

        return await catchUp.RunOnceAsync(Ct);
    }

    private static async Task<Settings> SettingsAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.Settings.AsNoTracking().SingleAsync(Ct);
    }

    private static async Task<Supporter> SupporterAsync(TestServices services, string memberId)
    {
        await using var db = services.Database.NewContext();
        return await db.Supporters.AsNoTracking().SingleAsync(s => s.PatreonMemberId == memberId, Ct);
    }

    // ── The catch-up ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCatchUpReadsEveryPage_FeedsEachPatronThroughTheIntake_AndMatchesTheConnectedDiscordAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services);

        var patreon = new Patreon((request, _) =>
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());

            if (url.Contains("/members", StringComparison.Ordinal) && url.Contains("page[cursor]=abc", StringComparison.Ordinal))
                return Json(PageJson([MemberJson("m3", "former_patron", "gold"), MemberJson("m4", "active_patron", "silver")], [UserJson("m3", null), UserJson("m4", "444")], null));

            if (url.Contains("/members", StringComparison.Ordinal))
                return Json(PageJson([MemberJson("m1", "active_patron", "gold"), MemberJson("m2", "active_patron", "silver")], [UserJson("m1", "111"), UserJson("m2", null)], "abc"));

            return Json(Campaigns);
        });
        var delays = new List<TimeSpan>();

        var result = await RunAsync(services, patreon, delays);

        Assert.True(result.CaughtUp);
        Assert.Equal(4, result.Members);

        // m3 is a former patron nobody counted: ignored. The others are active supporters.
        var m1 = await SupporterAsync(services, "m1");
        Assert.Equal("111", m1.DiscordUserId);
        Assert.Equal(SupporterStates.Active, m1.State);
        Assert.Equal(Charged, m1.LastPaidAt);
        Assert.Equal(Charged.AddMonths(1), m1.PaidThrough);
        Assert.Equal("Patron m1", m1.Name);

        var m2 = await SupporterAsync(services, "m2");
        Assert.Null(m2.DiscordUserId);
        Assert.NotNull(m2.DiscordLookupAt);

        Assert.Equal("444", (await SupporterAsync(services, "m4")).DiscordUserId);

        await using (var db = services.Database.NewContext())
        {
            Assert.Equal(3, await db.Supporters.CountAsync(Ct));
            Assert.Equal(["gold", "silver"], await db.SupporterTiers.OrderBy(t => t.Key).Select(t => t.Key).ToListAsync(Ct));
        }

        var settings = await SettingsAsync(services);
        Assert.Equal("camp-1", settings.PatreonCampaignId);
        Assert.Equal(services.Clock.UtcNow, settings.PatreonCatchUpAt);
        Assert.Equal(services.Clock.UtcNow + PatreonCatchUp.Every, settings.PatreonCatchUpDueAt);
        Assert.Null(settings.PatreonCatchUpProblem);

        // The list asked for the fields the intake reads and the user's connected apps, with the access token.
        var firstPage = patreon.Requests.First(r => r.Url.Contains("/members", StringComparison.Ordinal));
        Assert.Equal("Bearer access-1", firstPage.Authorization);
        Assert.Contains("fields[user]=social_connections", firstPage.Url, StringComparison.Ordinal);
        Assert.Contains("include=user,currently_entitled_tiers", firstPage.Url, StringComparison.Ordinal);
        Assert.Contains("last_charge_status", firstPage.Url, StringComparison.Ordinal);

        // Three requests (campaign, two pages), each a second after the one before.
        Assert.Equal(3, patreon.Requests.Count);
        Assert.Equal([PatreonLimits.MinGap, PatreonLimits.MinGap], delays);
    }

    [Fact]
    public async Task ADiscordAccountAnotherSupporterAlreadyHas_LeavesThePatronNotMatchedForStaff()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s => s.PatreonCampaignId = "camp-1");

        await using (var db = services.Database.NewContext())
        {
            db.Supporters.Add(new Supporter
            {
                Provider = SupporterProviders.Patreon,
                PayerId = "someone-else",
                Name = "Already matched",
                DiscordUserId = "111",
                State = SupporterStates.Active,
                FirstPaidAt = Charged,
                LastPaidAt = Charged,
                PaidThrough = Charged.AddMonths(1),
                UpdatedAt = Charged,
            });
            await db.SaveChangesAsync(Ct);
        }

        var patreon = new Patreon((_, _) => Json(PageJson([MemberJson("m1", "active_patron", "gold")], [UserJson("m1", "111")], null)));

        await RunAsync(services, patreon);

        var m1 = await SupporterAsync(services, "m1");
        Assert.Null(m1.DiscordUserId);
        Assert.Equal(SupporterStates.Active, m1.State);

        await using var check = services.Database.NewContext();
        Assert.Equal("Already matched", (await check.Supporters.AsNoTracking().SingleAsync(s => s.DiscordUserId == "111", Ct)).Name);
    }

    [Fact]
    public async Task ThePatronsAreReadAgainOnlyOnceADay()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s => s.PatreonCampaignId = "camp-1");

        var patreon = new Patreon((_, _) => Json(PageJson([MemberJson("m1", "active_patron", "gold")], [UserJson("m1", null)], null)));

        await RunAsync(services, patreon);
        var afterFirst = patreon.Requests.Count;

        // Nothing is waiting for a lookup (the catch-up already asked about m1) and the next catch-up is a day off.
        services.Clock.UtcNow += TimeSpan.FromHours(23);
        var second = await RunAsync(services, patreon);
        Assert.Equal(PatreonPassResult.Nothing, second);
        Assert.Equal(afterFirst, patreon.Requests.Count);

        services.Clock.UtcNow += TimeSpan.FromHours(2);
        var third = await RunAsync(services, patreon);
        Assert.True(third.CaughtUp);
        Assert.Equal(afterFirst + 1, patreon.Requests.Count);
    }

    [Fact]
    public async Task APatreonThatIsOffOrHasNoKeys_IsNeverAsked()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var patreon = new Patreon((_, _) => Json(Campaigns));

        await ConfigureAsync(services, s => s.PatreonOn = false);
        await RunAsync(services, patreon);

        await ConfigureAsync(services, s =>
        {
            s.PatreonOn = true;
            s.PatreonRefreshTokenEncrypted = null;
        });
        await RunAsync(services, patreon);

        Assert.Empty(patreon.Requests);
    }

    // ── The token ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APastedToken_WithNoKnownAge_IsReplacedFromTheRefreshTokenAndTheNewPairIsSaved()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s =>
        {
            s.PatreonTokenExpiresAt = null;
            s.PatreonCampaignId = "camp-1";
        });

        var patreon = new Patreon((request, _) => request.Method == HttpMethod.Post
            ? Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":2678400,"scope":"campaigns","token_type":"Bearer"}""")
            : Json(PageJson([], [], null)));

        var result = await RunAsync(services, patreon);

        Assert.True(result.CaughtUp);

        var refresh = patreon.Requests[0];
        Assert.Equal(HttpMethod.Post, refresh.Method);
        Assert.Equal("https://www.patreon.com/api/oauth2/token", refresh.Url);
        Assert.Contains("grant_type=refresh_token", refresh.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", refresh.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=client-id", refresh.Body, StringComparison.Ordinal);
        Assert.Contains("client_secret=client-secret", refresh.Body, StringComparison.Ordinal);

        // The list is asked for with the new token.
        Assert.Equal("Bearer access-2", patreon.Requests[1].Authorization);

        var settings = await SettingsAsync(services);
        var protector = new FakeSecretProtector();
        Assert.Equal("access-2", protector.Unprotect(settings.PatreonAccessTokenEncrypted));
        Assert.Equal("refresh-2", protector.Unprotect(settings.PatreonRefreshTokenEncrypted));
        Assert.Equal(services.Clock.UtcNow.AddSeconds(2678400), settings.PatreonTokenExpiresAt);
    }

    [Fact]
    public async Task ATokenThatRunsOutWithinDays_IsReplacedBeforeItDoes_AndOneWithTimeLeftIsNot()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s =>
        {
            s.PatreonTokenExpiresAt = services.Clock.UtcNow.AddDays(1);
            s.PatreonCampaignId = "camp-1";
        });

        var patreon = new Patreon((request, _) => request.Method == HttpMethod.Post
            ? Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":2678400}""")
            : Json(PageJson([], [], null)));

        await RunAsync(services, patreon);
        Assert.Equal(1, patreon.Requests.Count(r => r.Method == HttpMethod.Post));

        // A day later the token has 30 days left: not replaced again.
        services.Clock.UtcNow += PatreonCatchUp.Every + TimeSpan.FromMinutes(1);
        await RunAsync(services, patreon);
        Assert.Equal(1, patreon.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ARefusedRefreshToken_IsWrittenAsAProblem_AndTheCatchUpTriesAgainTomorrow()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s => s.PatreonTokenExpiresAt = null);

        var patreon = new Patreon((_, _) => Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

        var result = await RunAsync(services, patreon);

        Assert.False(result.CaughtUp);
        Assert.Single(patreon.Requests);
        var settings = await SettingsAsync(services);
        Assert.NotNull(settings.PatreonCatchUpProblem);
        Assert.Null(settings.PatreonCatchUpAt);
        Assert.Equal(services.Clock.UtcNow + PatreonCatchUp.Every, settings.PatreonCatchUpDueAt);
    }

    [Fact]
    public async Task A401_GetsOneNewToken_AndOneMoreTry()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s => s.PatreonCampaignId = "camp-1");

        var patreon = new Patreon((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":2678400}""");

            return request.Headers.Authorization?.Parameter == "access-2"
                ? Json(PageJson([], [], null))
                : Json("{}", HttpStatusCode.Unauthorized);
        });

        var result = await RunAsync(services, patreon);

        Assert.True(result.CaughtUp);
        Assert.Equal(3, patreon.Requests.Count);
    }

    // ── The rate limit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A429StopsTheCatchUp_IsNotRetried_AndHoldsEveryCallBackUntilTheStopEnds()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s => s.PatreonCampaignId = "camp-1");

        var limited = true;
        var patreon = new Patreon((_, _) => limited
            ? Json("""{"errors":[{"code":429}],"retry_after_seconds":60}""", HttpStatusCode.TooManyRequests)
            : Json(PageJson([MemberJson("m1", "active_patron", "gold")], [UserJson("m1", null)], null)));

        var first = await RunAsync(services, patreon);

        Assert.False(first.CaughtUp);
        Assert.Single(patreon.Requests);

        var stopped = await SettingsAsync(services);
        Assert.Equal(services.Clock.UtcNow + PatreonLimits.ColdStop, stopped.PatreonStoppedUntil);
        Assert.NotNull(stopped.PatreonCatchUpProblem);
        Assert.Null(stopped.PatreonCatchUpAt);

        // Not retried, and not asked again before the stop ends.
        services.Clock.UtcNow += TimeSpan.FromMinutes(14);
        Assert.Equal(PatreonPassResult.Nothing, await RunAsync(services, patreon));
        Assert.Single(patreon.Requests);

        // After it, the catch-up is the first thing to run, and clears the sentence.
        limited = false;
        services.Clock.UtcNow += TimeSpan.FromMinutes(2);
        var again = await RunAsync(services, patreon);

        Assert.True(again.CaughtUp);
        Assert.Equal(2, patreon.Requests.Count);
        Assert.Null((await SettingsAsync(services)).PatreonCatchUpProblem);
        Assert.Equal("m1", (await SupporterAsync(services, "m1")).PatreonMemberId);
    }

    [Fact]
    public void AStopLastsAsLongAsPatreonSays_ButNotLessThanFifteenMinutesNorMoreThanSixHours()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), PatreonLimits.StopFor(new PatreonFailure(PatreonProblem.Limited, 429)));
        Assert.Equal(TimeSpan.FromMinutes(15), PatreonLimits.StopFor(new PatreonFailure(PatreonProblem.Limited, 429, TimeSpan.FromSeconds(30))));
        Assert.Equal(TimeSpan.FromHours(1), PatreonLimits.StopFor(new PatreonFailure(PatreonProblem.Limited, 429, TimeSpan.FromHours(1))));
        Assert.Equal(TimeSpan.FromHours(6), PatreonLimits.StopFor(new PatreonFailure(PatreonProblem.Limited, 429, TimeSpan.FromDays(2))));
    }

    // ── The lookup ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APatronFromAWebhook_IsLookedUpOnce_AndMatchedToTheirConnectedDiscordAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s =>
        {
            s.PatreonCampaignId = "camp-1";
            s.PatreonCatchUpDueAt = services.Clock.UtcNow.AddDays(1);
        });

        // What a webhook brings: no connected apps, so no Discord account.
        var webhook = PatreonMessage.Parse(Encoding.UTF8.GetBytes(
            "{\"data\":" + MemberJson("m7", "active_patron", "gold") + ",\"included\":[" + Tiers + "]}")) ?? throw new InvalidOperationException();
        using (var scope = services.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<SupporterIntake>().TakePatreonAsync(webhook, "members:pledge:create", services.Clock.UtcNow, Ct);
        }

        var waiting = await SupporterAsync(services, "m7");
        Assert.Null(waiting.DiscordUserId);
        Assert.Null(waiting.DiscordLookupAt);

        var patreon = new Patreon((_, _) => Json(
            "{\"data\":" + MemberJson("m7", "active_patron", "gold") + ",\"included\":[" + Tiers + "," + UserJson("m7", "777") + "]}"));

        var result = await RunAsync(services, patreon);

        Assert.False(result.CaughtUp);
        Assert.Equal(1, result.Looked);
        Assert.Single(patreon.Requests);
        Assert.Contains("/members/m7?", patreon.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("fields[user]=social_connections", patreon.Requests[0].Url, StringComparison.Ordinal);

        var matched = await SupporterAsync(services, "m7");
        Assert.Equal("777", matched.DiscordUserId);
        Assert.NotNull(matched.DiscordLookupAt);

        // Asked about once: the next run has nothing waiting.
        Assert.Equal(PatreonPassResult.Nothing, await RunAsync(services, patreon));
        Assert.Single(patreon.Requests);
    }

    [Fact]
    public async Task APatronWithNoConnectedDiscord_IsNotLookedUpAgainUntilTheNextCatchUp()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s =>
        {
            s.PatreonCampaignId = "camp-1";
            s.PatreonCatchUpDueAt = services.Clock.UtcNow.AddDays(1);
        });

        var webhook = PatreonMessage.Parse(Encoding.UTF8.GetBytes(
            "{\"data\":" + MemberJson("m8", "active_patron", "gold") + ",\"included\":[" + Tiers + "]}")) ?? throw new InvalidOperationException();
        using (var scope = services.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<SupporterIntake>().TakePatreonAsync(webhook, "members:pledge:create", services.Clock.UtcNow, Ct);
        }

        var patreon = new Patreon((_, _) => Json(
            "{\"data\":" + MemberJson("m8", "active_patron", "gold") + ",\"included\":[" + Tiers + "," + UserJson("m8", null) + "]}"));

        await RunAsync(services, patreon);
        await RunAsync(services, patreon);

        Assert.Single(patreon.Requests);
        var supporter = await SupporterAsync(services, "m8");
        Assert.Null(supporter.DiscordUserId);
        Assert.NotNull(supporter.DiscordLookupAt);
    }

    [Fact]
    public async Task AMemberPatreonNoLongerHas_IsNotAProblem_AndIsNotAskedAboutAgain()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await ConfigureAsync(services, s =>
        {
            s.PatreonCampaignId = "camp-1";
            s.PatreonCatchUpDueAt = services.Clock.UtcNow.AddDays(1);
        });

        var webhook = PatreonMessage.Parse(Encoding.UTF8.GetBytes(
            "{\"data\":" + MemberJson("m9", "active_patron", "gold") + ",\"included\":[" + Tiers + "]}")) ?? throw new InvalidOperationException();
        using (var scope = services.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<SupporterIntake>().TakePatreonAsync(webhook, "members:pledge:create", services.Clock.UtcNow, Ct);
        }

        var patreon = new Patreon((_, _) => Json("{}", HttpStatusCode.NotFound));

        await RunAsync(services, patreon);
        await RunAsync(services, patreon);

        Assert.Single(patreon.Requests);
        Assert.Null((await SettingsAsync(services)).PatreonCatchUpProblem);
        Assert.NotNull((await SupporterAsync(services, "m9")).DiscordLookupAt);
    }
}
