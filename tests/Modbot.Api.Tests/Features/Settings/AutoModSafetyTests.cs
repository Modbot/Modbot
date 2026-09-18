using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Moderation;
using Modbot.Moderation;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// What stands between a moderation rule matching and a moderation rule acting: the test set and
/// the gate (design §12), the trial, the automatic pause and scope (§13), and rule versions (§14).
/// </summary>
/// <remarks>
/// Over the real database, with a fake Discord and a fake AI endpoint, because every one of these
/// is a question about rows: what the gate read, what the trial recorded, what the pause counted.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AutoModSafetyTests
{
    private const string Path = "/api/settings/automod";

    private const string Guild = "900000000000000001";
    private const string Channel = "900000000000000002";
    private const string OtherChannel = "900000000000000003";
    private const string StaffRole = "900000000000000004";

    private readonly PostgresFixture _db;

    public AutoModSafetyTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The gate ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleCannotStartActingUntilATestRunCatchesEverythingAndFlagsNothingWrongly()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = await ListAsync(host, cookie, "Scams", "free nitro");

        // The existing term's own id, echoed back on every PUT below exactly as a real form
        // would: a term sent with no id reads as a new one, which is a text change and would
        // start a new version -- one the clean run below never tested.
        var termId = await TermIdAsync(host, cookie, id);

        // No test set at all.
        var refused = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, termId: termId), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("test set", await refused.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // A sample the rule wrongly flags: still refused.
        await SampleAsync(host, cookie, id, "get free nitro here", shouldFlag: true);
        await SampleAsync(host, cookie, id, "free nitro is a scam, do not click", shouldFlag: false);

        var wrong = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/tests/run", null, cookie, Ct));
        Assert.Equal(1, wrong.GetProperty("caught").GetInt32());
        Assert.Equal(1, wrong.GetProperty("wronglyFlagged").GetInt32());

        var stillRefused = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, termId: termId), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, stillRefused.StatusCode);

        // Take the sample the rule was always going to flag out, run again, and the gate opens.
        var samples = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/rules/termList/{id}/tests", null, cookie, Ct));
        var bad = samples.GetProperty("samples").EnumerateArray().First(s => !s.GetProperty("shouldFlag").GetBoolean());
        await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/rules/termList/{id}/samples/{bad.GetProperty("id").GetGuid()}", null, cookie, Ct);

        // The clock has to move between the two runs: "the newest run" is read back by its time,
        // and a fake clock that never ticks would leave both runs tied at the same instant.
        host.Clock.Advance(TimeSpan.FromSeconds(1));

        var clean = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/tests/run", null, cookie, Ct));
        Assert.Equal(0, clean.GetProperty("wronglyFlagged").GetInt32());

        var allowed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, termId: termId), cookie, Ct));
        Assert.True(allowed.GetProperty("list").GetProperty("tests").GetProperty("passes").GetBoolean());
    }

    private static async Task<string> TermIdAsync(ApiTestHost host, string cookie, Guid listId)
    {
        var detail = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/lists/{listId}", null, cookie, Ct));
        return detail.GetProperty("terms")[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ChangingTheRuleAfterAPassingRunClosesTheGateAgain()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = await ListAsync(host, cookie, "Scams", "free nitro");
        await SampleAsync(host, cookie, id, "get free nitro here", shouldFlag: true);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/tests/run", null, cookie, Ct);

        // The terms change and the action is set in the same save: the run was of another rule.
        var refused = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free discord nitro", delete: true), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task AnOperatorMayOverrideTheGate_AndTheOverrideIsAFactNamingThem()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = await ListAsync(host, cookie, "Scams", "free nitro");

        var saved = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, withoutTest: true), cookie, Ct));

        Assert.False(saved.GetProperty("list").GetProperty("tests").GetProperty("passes").GetBoolean());

        var facts = await host.FactsAsync(FactType.AutoModRuleChanged, user.Id.ToString(), Ct);
        var overrides = facts.Select(ApiTestHost.DataOf)
            .Where(d => d.GetProperty("change").GetString() == "act-without-test")
            .ToList();

        var only = Assert.Single(overrides);
        Assert.Equal(user.Username, only.GetProperty("username").GetString());
        Assert.True(only.GetProperty("deleteMessage").GetBoolean());
    }

    // ── The trial ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleInItsTrialRecordsWhatItWouldHaveDone_AndActsOnlyOnceTheOperatorEndsIt()
    {
        var discord = new AutoModTests.FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        var id = await ListAsync(host, cookie, "Scams", "free nitro");
        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, timeout: 30, withoutTest: true, trialDays: 14), cookie, Ct);

        var outcome = await CheckAsync(host, Message("m1", "author-1", "free nitro here"));

        Assert.Equal(1, outcome.FlagsWritten);
        Assert.False(outcome.MessageDeleted);
        Assert.Null(outcome.TimedOutMinutes);
        Assert.Empty(discord.Deleted);
        Assert.Empty(discord.TimedOut);
        Assert.True(Assert.Single(outcome.Matches).Trial);

        await using (var db = _db.NewContext())
        {
            var flag = await db.ModerationFlags.SingleAsync(Ct);
            Assert.True(flag.Trial);
            Assert.True(flag.WouldDeleteMessage);
            Assert.Equal(30, flag.WouldTimeOutMinutes);
            Assert.False(flag.MessageDeleted);
        }

        var card = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        var trial = card.GetProperty("lists")[0].GetProperty("trial");
        Assert.Equal(14, trial.GetProperty("days").GetInt32());
        Assert.Equal(1, trial.GetProperty("flags").GetInt32());
        Assert.Equal(1, trial.GetProperty("wouldDelete").GetInt32());
        Assert.Equal(1, trial.GetProperty("wouldTimeOut").GetInt32());
        Assert.False(card.GetProperty("lists")[0].GetProperty("acting").GetBoolean());

        var ended = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct));
        Assert.True(ended.GetProperty("list").GetProperty("acting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ended.GetProperty("list").GetProperty("trial").ValueKind);

        var now = await CheckAsync(host, Message("m2", "author-2", "free nitro here"));
        Assert.True(now.MessageDeleted);
        Assert.Equal(30, now.TimedOutMinutes);
        Assert.Single(discord.Deleted);
    }

    [Fact]
    public async Task EndingATrialIsAFact_AndARuleNotInATrialCannotEndOne()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = await ListAsync(host, cookie, "Scams", "free nitro");

        var notInOne = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notInOne.StatusCode);

        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true, withoutTest: true), cookie, Ct);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct);

        var changes = (await host.FactsAsync(FactType.AutoModRuleChanged, user.Id.ToString(), Ct))
            .Select(ApiTestHost.DataOf)
            .Where(d => d.GetProperty("change").GetString() == "trial-ended")
            .ToList();

        Assert.Single(changes);
        Assert.True(changes[0].GetProperty("deleteMessage").GetBoolean());
    }

    // ── The automatic pause ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleThatActsFarMoreInAnHourThanUsualPausesItself_AndAnOperatorResumesIt()
    {
        var discord = new AutoModTests.FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        var id = await ActingListAsync(host, cookie, "Scams", "free nitro", delete: true);

        // From a standing start the first condition decides: the eleventh action in an hour stops it.
        for (var i = 0; i < RunawayGuard.ActionsInAnHour; i++)
            await CheckAsync(host, Message($"m{i}", $"author-{i}", "free nitro here"));

        await using (var db = _db.NewContext())
            Assert.Null((await db.ModerationTermLists.SingleAsync(Ct)).PausedAt);

        var eleventh = await CheckAsync(host, Message("m-last", "author-last", "free nitro here"));
        Assert.True(eleventh.MessageDeleted);

        await using (var db = _db.NewContext())
        {
            var list = await db.ModerationTermLists.SingleAsync(Ct);
            Assert.NotNull(list.PausedAt);
            Assert.Contains("11 times in an hour", list.PausedReason, StringComparison.Ordinal);
        }

        var paused = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AutoModRulePaused, id.ToString(), Ct)));
        Assert.Equal(11, paused.GetProperty("actionsInTheLastHour").GetInt32());
        Assert.Equal("Scams", paused.GetProperty("ruleName").GetString());

        // Paused means it still flags and no longer acts.
        var deletedSoFar = discord.Deleted.Count;
        var whilePaused = await CheckAsync(host, Message("m-after", "author-after", "free nitro here"));
        Assert.Equal(1, whilePaused.FlagsWritten);
        Assert.False(whilePaused.MessageDeleted);
        Assert.Equal(deletedSoFar, discord.Deleted.Count);

        // The Health page says so.
        var (_, operatorCookie) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var health = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/health/sync", null, operatorCookie, Ct));
        var row = Assert.Single(health.GetProperty("pausedRules").EnumerateArray());
        Assert.Equal("Scams", row.GetProperty("ruleName").GetString());

        var resumed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/resume", null, cookie, Ct));
        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("list").GetProperty("paused").ValueKind);
        Assert.True(resumed.GetProperty("list").GetProperty("acting").GetBoolean());

        var after = await CheckAsync(host, Message("m-resumed", "author-resumed", "free nitro here"));
        Assert.True(after.MessageDeleted);
    }

    [Theory]
    // From a standing start: ten is not enough, eleven is.
    [InlineData(10, 0, false)]
    [InlineData(11, 0, true)]
    // A rule that normally acts twice an hour: 336 in seven days is an average of 2, and the
    // second condition does not hold until 33 an hour (AI moderation design §13.2).
    [InlineData(8, 336, false)]
    [InlineData(11, 336, false)]
    [InlineData(32, 336, false)]
    [InlineData(33, 336, true)]
    public void TheRunawayNumbersWorkFromAStandingStartAndFromAHistory(int hour, int week, bool pauses)
        => Assert.Equal(pauses, RunawayGuard.ShouldPause(hour, week));

    // ── Scope ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChannelLimitStopsTheRuleEntirely_AndAnExemptRoleStopsOnlyTheAction()
    {
        var discord = new AutoModTests.FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        var id = await ActingListAsync(host, cookie, "Scams", "free nitro", delete: true,
            scope: new { channelMode = "only", channels = new[] { Channel }, exemptRoles = new[] { StaffRole }, exemptRolesSkipFlag = false });

        await MemberAsync("staff-1", StaffRole);

        // Another channel: not this rule's business at all.
        var elsewhere = await CheckAsync(host, Message("m1", "author-1", "free nitro here") with { ChannelId = OtherChannel });
        Assert.Empty(elsewhere.Matches);

        // The right channel, somebody with the exempt role: flagged, not acted on.
        var staff = await CheckAsync(host, Message("m2", "staff-1", "free nitro here"));
        Assert.Equal(1, staff.FlagsWritten);
        Assert.False(staff.MessageDeleted);
        Assert.True(Assert.Single(staff.Matches).Exempt);
        Assert.Empty(discord.Deleted);

        // Everybody else in that channel is acted on.
        var member = await CheckAsync(host, Message("m3", "member-1", "free nitro here"));
        Assert.True(member.MessageDeleted);

        // With "do not flag them either", the exempt person is not flagged at all.
        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", delete: true,
                scope: new { channelMode = "only", channels = new[] { Channel }, exemptRoles = new[] { StaffRole }, exemptRolesSkipFlag = true }),
            cookie, Ct);

        var quiet = await CheckAsync(host, Message("m4", "staff-1", "free nitro here"));
        Assert.Empty(quiet.Matches);
        Assert.Equal(0, quiet.FlagsWritten);
    }

    [Fact]
    public async Task AllButTheseChannelsIsTheOtherWayRound()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "free nitro",
            scope: new { channelMode = "except", channels = new[] { Channel }, exemptRoles = Array.Empty<string>(), exemptRolesSkipFlag = false });

        Assert.Empty((await CheckAsync(host, Message("m1", "a", "free nitro here"))).Matches);
        Assert.Single((await CheckAsync(host, Message("m2", "a", "free nitro here") with { ChannelId = OtherChannel })).Matches);
    }

    [Fact]
    public async Task AChannelLimitWithNoChannelsIsRefused()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Scams", "free nitro",
                scope: new { channelMode = "only", channels = Array.Empty<string>(), exemptRoles = Array.Empty<string>(), exemptRolesSkipFlag = false }),
            cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ── Rule versions ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFlagAndAnActionNameTheVersionTheyActedOn_AndTheFlagShowsTheRuleAsItWasThen()
    {
        var discord = new AutoModTests.FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        var id = await ActingListAsync(host, cookie, "Scams", "free nitro", delete: true);

        await CheckAsync(host, Message("m1", "author-1", "free nitro here"));

        // The rule is rewritten afterwards. The flag still has to read as the rule that wrote it.
        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}", List("Scams", "something else", delete: true), cookie, Ct);

        var versions = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/rules/termList/{id}/versions", null, cookie, Ct));
        Assert.Equal(2, versions.GetProperty("versions").GetArrayLength());
        Assert.Equal("something else", versions.GetProperty("versions")[0].GetProperty("text").GetString());
        Assert.Equal("free nitro", versions.GetProperty("versions")[1].GetProperty("text").GetString());

        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var flags = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/moderation-flags", null, reader, Ct));
        var flag = flags.GetProperty("flags")[0];
        Assert.Equal(1, flag.GetProperty("ruleVersion").GetInt32());
        Assert.Equal("free nitro", flag.GetProperty("ruleText").GetString());

        var deleted = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AutoModMessageDeleted, "author-1", Ct)));
        Assert.Equal(1, Assert.Single(deleted.GetProperty("rules").EnumerateArray()).GetProperty("ruleVersion").GetInt32());
    }

    [Fact]
    public async Task SwitchingARuleOnAndOffIsNotANewVersion()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var created = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Scams", "free nitro"), cookie, Ct));
        var id = created.GetProperty("list").GetProperty("id").GetGuid();

        // The existing term's own id, echoed back exactly as a real form would: a term sent with
        // no id at all reads as a new one, which is a text change and would be its own version.
        var termId = created.GetProperty("terms")[0].GetProperty("id").GetString();

        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", "free nitro", enabled: false, termId: termId), cookie, Ct);

        var versions = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/rules/termList/{id}/versions", null, cookie, Ct));
        Assert.Equal(1, versions.GetProperty("versions").GetArrayLength());
    }

    // ── Test sets ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewAiTopicStartsWithTheInjectionSamples_AndARunOfThemCostsOneRequestEach()
    {
        var replies = 0;
        var ai = new AutoModTests.FakeAi(_ =>
        {
            replies++;
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai: ai);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        // A test run costs a real call whether or not moderation is switched on (an operator earns
        // the right to act before turning a rule on), but it still needs the daily call limit's
        // settings row to exist -- otherwise there is nothing for the limit check to find, and it
        // reads exactly like the limit being spent.
        await SwitchOnAsync(host, cookie);

        var topic = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/topics", new
        {
            name = "Scams",
            instructions = "Offers of free things that ask for a login.",
            sensitivity = "medium",
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
        }, cookie, Ct));

        var id = topic.GetProperty("id").GetGuid();

        var tests = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/rules/topic/{id}/tests", null, cookie, Ct));
        var samples = tests.GetProperty("samples").EnumerateArray().ToList();
        Assert.Equal(AutoModRuleHistory.InjectionSamples.Count, samples.Count);
        Assert.All(samples, s => Assert.False(s.GetProperty("shouldFlag").GetBoolean()));
        Assert.All(samples, s => Assert.True(s.GetProperty("seeded").GetBoolean()));
        Assert.Contains(samples, s => s.GetProperty("text").GetString()!.Contains("Ignore all previous instructions", StringComparison.Ordinal));

        var run = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/topic/{id}/tests/run", null, cookie, Ct));
        Assert.Equal(samples.Count, run.GetProperty("samples").GetInt32());
        Assert.Equal(0, run.GetProperty("wronglyFlagged").GetInt32());
        Assert.Equal("m", run.GetProperty("model").GetString());
        Assert.Equal(user.Username, run.GetProperty("ranBy").GetString());
        Assert.Equal(samples.Count, replies);

        // The run is charged to moderation like any other AI call (design §12.2).
        await using var db = _db.NewContext();
        Assert.Equal(samples.Count, await db.AiUsage.CountAsync(u => u.Feature == "moderation", Ct));
    }

    [Fact]
    public async Task ATestRunStopsAtTheDailyAiCallLimit_AndSaysSo()
    {
        var ai = new AutoModTests.FakeAi(_ => """{"matches":[]}""");

        await using var host = await StartAsync(ai: ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true, dailyAiCallLimit = 1 }, cookie, Ct);

        var topic = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/topics", new
        {
            name = "Scams",
            instructions = "Offers of free things that ask for a login.",
            sensitivity = "medium",
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
        }, cookie, Ct));

        var id = topic.GetProperty("id").GetGuid();
        var run = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/topic/{id}/tests/run", null, cookie, Ct));

        Assert.Equal("The daily AI call limit is reached.", run.GetProperty("aiSkipped").GetString());
    }

    [Fact]
    public async Task ARunOfAnEmptyTestSetIsRefused_AndEveryTestSetRouteNeedsChangeSettings()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var id = await ListAsync(host, cookie, "Scams", "free nitro");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/tests/run", null, cookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Get, $"{Path}/rules/termList/{Guid.NewGuid()}/tests", null, cookie, Ct)).StatusCode);

        foreach (var (method, path) in new (HttpMethod, string)[]
                 {
                     (HttpMethod.Get, $"{Path}/rules/termList/{id}/tests"),
                     (HttpMethod.Post, $"{Path}/rules/termList/{id}/tests/run"),
                     (HttpMethod.Get, $"{Path}/rules/termList/{id}/versions"),
                     (HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial"),
                     (HttpMethod.Post, $"{Path}/rules/termList/{id}/resume"),
                 })
        {
            var response = await host.SendJsonAsync(method, path, null, reader, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var added = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/samples",
            new { text = "hello", shouldFlag = false, note = (string?)null, target = "discordMessage" }, reader, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, added.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<ApiTestHost> StartAsync(
        AutoModTests.FakeDiscordActions? discord = null, AutoModTests.FakeAi? ai = null)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using (var db = _db.NewContext())
        {
            await db.ModerationFlags.ExecuteDeleteAsync(Ct);
            await db.ModerationTestSamples.ExecuteDeleteAsync(Ct);
            await db.ModerationTestRuns.ExecuteDeleteAsync(Ct);
            await db.ModerationRuleVersions.ExecuteDeleteAsync(Ct);
            await db.ModerationTermLists.ExecuteDeleteAsync(Ct);
            await db.ModerationTopics.ExecuteDeleteAsync(Ct);
            await db.DiscordMembers.ExecuteDeleteAsync(Ct);
            await db.AiUsage.ExecuteDeleteAsync(Ct);
            await db.AiFeatureLimits.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (discord is not null)
                services.AddSingleton<IDiscordModerationActions>(discord);
            if (ai is not null)
                services.AddScoped<IAiClients>(_ => ai);
        });
    }

    private async Task MemberAsync(string userId, params string[] roles)
    {
        await using var db = _db.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = userId,
            DisplayName = userId,
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SwitchOnAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true, dailyAiCallLimit = 200 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> ListAsync(
        ApiTestHost host, string cookie, string name, string term,
        bool delete = false, int? timeout = null, object? scope = null, bool withoutTest = false)
    {
        var created = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List(name, term, delete: delete, timeout: timeout, scope: scope, withoutTest: withoutTest || delete || timeout is not null),
            cookie, Ct));

        return created.GetProperty("list").GetProperty("id").GetGuid();
    }

    private async Task<Guid> ActingListAsync(
        ApiTestHost host, string cookie, string name, string term,
        bool delete = false, int? timeout = null, object? scope = null)
    {
        var id = await ListAsync(host, cookie, name, term, delete, timeout, scope);
        var ended = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        return id;
    }

    private async Task SampleAsync(ApiTestHost host, string cookie, Guid ruleId, string text, bool shouldFlag)
    {
        var added = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{ruleId}/samples",
            new { text, shouldFlag, note = (string?)null, target = "discordMessage" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    }

    private static object List(
        string name, string term, bool enabled = true, bool delete = false, int? timeout = null,
        object? scope = null, bool withoutTest = false, int? trialDays = null, string? termId = null) => new
        {
            name,
            enabled,
            targets = new[] { "discordMessage" },
            deleteMessage = delete,
            timeoutMinutes = timeout,
            terms = new[] { new { id = termId, kind = "word", text = term } },
            scope,
            trialDays,
            actWithoutTest = withoutTest,
        };

    private static async Task<ModerationOutcome> CheckAsync(ApiTestHost host, DiscordMessageToCheck message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationChecker>().CheckDiscordMessageAsync(message, Ct);
    }

    private static DiscordMessageToCheck Message(string id, string author, string text)
        => new(Guild, Channel, id, author, author, text);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
