using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Moderation;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.TestSupport;
using OpenAI;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// AI moderation end to end over the real database: rules saved through the API, the engine run
/// against them, flags, dismissals, Discord actions and the facts that record them. Discord and the
/// AI endpoint are fakes; nothing leaves the process.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiModerationTests
{
    private const string Path = "/api/settings/ai/moderation";

    private const string Guild = "900000000000000001";
    private const string Channel = "900000000000000002";

    private readonly PostgresFixture _db;

    public AiModerationTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManagingRulesNeedsTheSettingsPermission_AndDismissingNeedsReviewTickets()
    {
        await using var host = await StartAsync();
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/moderation-flags", null, reader, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/moderation-flags/{Guid.NewGuid()}/dismiss", null, reader, Ct)).StatusCode);
    }

    [Fact]
    public async Task ANewDeploymentHasModerationOffWithNoRules()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(200, body.GetProperty("dailyAiCallLimit").GetInt32());
        Assert.Empty(body.GetProperty("lists").EnumerateArray());
        Assert.Empty(body.GetProperty("topics").EnumerateArray());
    }

    [Fact]
    public async Task ABrokenPatternIsRefused_AndActingOnProfileTextIsRefused()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var broken = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Bad", [Term("regex", "(open")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);

        var profileAction = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Profiles", [Term("word", "spam")], targets: ["bio"], delete: true), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, profileAction.StatusCode);
        Assert.Contains("Discord messages", await profileAction.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingAListToActRecordsWhoSetIt_AndGoingBackToFlagOnlyClearsIt()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var created = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Scams", [Term("word", "free nitro")], delete: true, timeout: 60), cookie, Ct));

        var list = created.GetProperty("list");
        Assert.Equal(user.Username, list.GetProperty("setToActBy").GetString());
        var id = list.GetProperty("id").GetGuid();

        var flagOnly = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Scams", [Term("word", "free nitro")]), cookie, Ct));
        Assert.Equal(JsonValueKind.Null, flagOnly.GetProperty("list").GetProperty("setToActBy").ValueKind);

        var facts = await host.FactsAsync(FactType.AiModerationRuleChanged, user.Id.ToString(), Ct);
        Assert.Equal(2, facts.Count);
        Assert.True(ApiTestHost.DataOf(facts[^1]).GetProperty("deleteMessage").GetBoolean());
    }

    [Fact]
    public async Task TryItShowsWhatWouldHappen_AndRecordsNothing()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Scams", [Term("word", "free nitro")], delete: true, timeout: 30), cookie, Ct);

        var tried = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/try",
            new { text = "Get FREE N1TRO now", target = "discordMessage", includeAi = false }, cookie, Ct));

        var match = Assert.Single(tried.GetProperty("matches").EnumerateArray());
        Assert.Equal("FREE N1TRO", match.GetProperty("matched").GetString());
        Assert.True(tried.GetProperty("wouldDeleteMessage").GetBoolean());
        Assert.Equal(30, tried.GetProperty("wouldTimeOutMinutes").GetInt32());

        await using var db = _db.NewContext();
        Assert.Equal(0, await db.ModerationFlags.CountAsync(Ct));
    }

    [Fact]
    public async Task NothingHappensWhileModerationIsOff()
    {
        var discord = new FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Scams", [Term("word", "free nitro")], delete: true), cookie, Ct);

        var outcome = await CheckAsync(host, Message("m1", "u1", "free nitro here"));

        Assert.Empty(outcome.Matches);
        Assert.Empty(discord.Deleted);
    }

    [Fact]
    public async Task AMatchingMessageIsFlaggedDeletedAndTimedOut_AndTheFactsNameTheOperatorWhoSetTheRuleToAct()
    {
        var discord = new FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Scams", [Term("word", "free nitro")], delete: true, timeout: 45), cookie, Ct);

        var outcome = await CheckAsync(host, Message("m1", "author-1", "hey FREE nitro at this link"));

        Assert.Equal(1, outcome.FlagsWritten);
        Assert.True(outcome.MessageDeleted);
        Assert.Equal(45, outcome.TimedOutMinutes);
        Assert.Equal((Channel, "m1"), Assert.Single(discord.Deleted));
        Assert.Equal((Guild, "author-1", TimeSpan.FromMinutes(45)), Assert.Single(discord.TimedOut));

        await using (var db = _db.NewContext())
        {
            var flag = await db.ModerationFlags.SingleAsync(Ct);
            Assert.Equal("FREE nitro", flag.Matched);
            Assert.Equal(FactPlatform.Discord, flag.SubjectPlatform);
            Assert.True(flag.MessageDeleted);
            Assert.Equal(45, flag.TimedOutMinutes);
        }

        Assert.Single(await host.FactsAsync(FactType.AiModerationFlag, "author-1", Ct));

        var deleted = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AiModerationMessageDeleted, "author-1", Ct)));
        Assert.True(deleted.GetProperty("done").GetBoolean());
        Assert.Equal("m1", deleted.GetProperty("messageId").GetString());
        var rule = Assert.Single(deleted.GetProperty("rules").EnumerateArray());
        Assert.Equal(user.Username, rule.GetProperty("setToActByUsername").GetString());
        Assert.Equal(user.Id.ToString(), rule.GetProperty("setToActByUserId").GetString());

        var timedOut = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AiModerationTimeout, "author-1", Ct)));
        Assert.Equal(45, timedOut.GetProperty("minutes").GetInt32());

        // The same message edited: already flagged, so nothing again.
        var again = await CheckAsync(host, Message("m1", "author-1", "hey FREE nitro at this link!!") with { Edited = true });
        Assert.Equal(0, again.FlagsWritten);
        Assert.Single(discord.Deleted);
    }

    [Fact]
    public async Task ADismissedFlagNeverComesBackForThatPerson_ButStillFlagsSomebodyElse()
    {
        var discord = new FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (reviewer, reviewerCookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile | ModbotPermissions.ReviewTickets, Ct);

        await SwitchOnAsync(host, admin);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Words", [Term("word", "scunthorpe")], delete: true), admin, Ct);

        await CheckAsync(host, Message("m1", "local-1", "I live in Scunthorpe"));
        Assert.Single(discord.Deleted);

        var flags = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/moderation-flags", null, reviewerCookie, Ct));
        Assert.Equal(1, flags.GetProperty("open").GetInt32());
        var flagId = flags.GetProperty("flags")[0].GetProperty("id").GetGuid();

        var dismissed = await host.SendJsonAsync(HttpMethod.Post, $"/api/moderation-flags/{flagId}/dismiss", null, reviewerCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        var fact = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AiModerationFlagDismissed, "local-1", Ct)));
        Assert.Equal(flagId.ToString(), fact.GetProperty("flagId").GetString());

        var later = await CheckAsync(host, Message("m2", "local-1", "Scunthorpe again"));
        Assert.True(Assert.Single(later.Matches).Suppressed);
        Assert.Equal(0, later.FlagsWritten);
        Assert.Single(discord.Deleted);

        var other = await CheckAsync(host, Message("m3", "someone-else", "Scunthorpe"));
        Assert.Equal(1, other.FlagsWritten);

        var settings = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, admin, Ct));
        var stats = settings.GetProperty("lists")[0].GetProperty("stats");
        Assert.Equal(2, stats.GetProperty("flags").GetInt32());
        Assert.Equal(1, stats.GetProperty("dismissed").GetInt32());

        Assert.NotEqual(Guid.Empty, reviewer.Id);
    }

    [Fact]
    public async Task AProfileIsFlaggedButNeverActedOn()
    {
        var discord = new FakeDiscordActions();
        await using var host = await StartAsync(discord);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Ads", [Term("contains", "discord.gg")], targets: ["discordMessage", "bio"], delete: true, timeout: 10), cookie, Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IModerationChecker>()
            .CheckProfileAsync(new ProfileToCheck("usr_profile_1", "Name", "join discord.gg/abc", null, null), Ct);

        Assert.Equal(1, outcome.FlagsWritten);
        Assert.False(outcome.MessageDeleted);
        Assert.Empty(discord.Deleted);
        Assert.Empty(discord.TimedOut);

        await using var db = _db.NewContext();
        var flag = await db.ModerationFlags.SingleAsync(Ct);
        Assert.Equal("bio", flag.Target);
        Assert.Equal(FactPlatform.VRChat, flag.SubjectPlatform);
    }

    [Fact]
    public async Task AiTopicsRunOnlyOnTextNoTermListMatched_AndStopAtTheDailyLimit()
    {
        var requests = new List<string>();
        var ai = new FakeAi(body =>
        {
            requests.Add(body);
            return JsonSerializer.Serialize(new
            {
                matches = new[] { new { topic = "t1", why = "Asks people to vote.", quote = "vote for me" } },
            });
        });

        await using var host = await StartAsync(ai: ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true, dailyAiCallLimit = 1 }, cookie, Ct);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Words", [Term("word", "spam")]), cookie, Ct);
        var topic = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/topics", new
        {
            name = "Politics",
            instructions = "Election campaigning.",
            sensitivity = "medium",
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, topic.StatusCode);

        var byList = await CheckAsync(host, Message("m1", "a", "spam, and vote for me"));
        Assert.Equal(ModerationRuleKind.TermList, Assert.Single(byList.Matches).RuleKind);
        Assert.Empty(requests);

        var byTopic = await CheckAsync(host, Message("m2", "a", "please vote for me"));
        var match = Assert.Single(byTopic.Matches);
        Assert.Equal(ModerationRuleKind.Topic, match.RuleKind);
        Assert.Equal("vote for me", match.Matched);
        Assert.Equal("Asks people to vote.", match.Reason);
        Assert.Single(requests);

        var overLimit = await CheckAsync(host, Message("m3", "b", "vote for me too"));
        Assert.Empty(overLimit.Matches);
        Assert.Equal("The daily AI call limit is reached.", overLimit.AiSkipped);
        Assert.Single(requests);

        // The one call that went out is recorded against moderation, with nobody's name on it.
        await using var db = _db.NewContext();
        var usage = await db.AiUsage.SingleAsync(Ct);
        Assert.Equal("moderation", usage.Feature);
        Assert.Null(usage.UserId);
        Assert.Equal("m", usage.Model);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(100, usage.CachedInputTokens);
    }

    [Fact]
    public async Task AtTheModerationSpendLimitAiTopicsStop_ButTermListsKeepRunning()
    {
        var requests = 0;
        var ai = new FakeAi(_ =>
        {
            requests++;
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai: ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Words", [Term("word", "spam")]), cookie, Ct);
        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/topics", new
        {
            name = "Anything",
            instructions = "Anything at all.",
            sensitivity = "high",
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
        }, cookie, Ct);

        await using (var db = _db.NewContext())
        {
            db.AiFeatureLimits.Add(new AiFeatureLimit { Feature = "moderation", MonthlyTokenLimit = 100 });
            db.AiUsage.Add(new AiUsage
            {
                At = host.Clock.UtcNow,
                Feature = "moderation",
                Model = "m",
                InputTokens = 80,
                OutputTokens = 20,
            });
            await db.SaveChangesAsync(Ct);
        }

        var outcome = await CheckAsync(host, Message("m1", "a", "hello there"));
        Assert.Equal("The AI spend limit for moderation is reached.", outcome.AiSkipped);
        Assert.Equal(0, requests);

        var byList = await CheckAsync(host, Message("m2", "a", "spam"));
        Assert.Equal(1, byList.FlagsWritten);
    }

    [Fact]
    public async Task AHubListIsAdded_AndANewerVersionWaitsUntilTheOperatorAppliesIt()
    {
        var version = "2026.9.0";
        var terms = """[{"id":"r1","type":"term","category":"x","match":"spam"}]""";

        await using var host = await StartAsync(configure: services =>
            services.AddHttpClient(HubTermLists.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(_ =>
                Json($$"""{"id":"sample_list","name":"Sample","version":"{{version}}","rules":{{terms}}}"""))));

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var added = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists/hub", new { hubId = "sample_list" }, cookie, Ct));
        var id = added.GetProperty("list").GetProperty("id").GetGuid();
        Assert.Equal("2026.9.0", added.GetProperty("list").GetProperty("hubVersion").GetString());

        Assert.Equal(HttpStatusCode.Conflict,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists/hub", new { hubId = "sample_list" }, cookie, Ct)).StatusCode);

        version = "2026.9.1";
        terms = """[{"id":"r1","type":"term","category":"x","match":"spam"},{"id":"r2","type":"term","category":"x","match":"eggs"}]""";

        var refreshed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists/{id}/refresh", null, cookie, Ct));
        Assert.Equal("2026.9.1", refreshed.GetProperty("list").GetProperty("hubAvailableVersion").GetString());
        Assert.Equal(1, refreshed.GetProperty("list").GetProperty("hubAvailableChanges").GetProperty("added").GetInt32());
        Assert.Single(refreshed.GetProperty("terms").EnumerateArray());

        var applied = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists/{id}/update", null, cookie, Ct));
        Assert.Equal("2026.9.1", applied.GetProperty("list").GetProperty("hubVersion").GetString());
        Assert.Equal(2, applied.GetProperty("terms").GetArrayLength());

        var excluded = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}", new
        {
            name = "ignored",
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
            excludedTerms = new[] { "r2", "not-a-term" },
        }, cookie, Ct));
        Assert.Equal("Sample", excluded.GetProperty("list").GetProperty("name").GetString());
        Assert.Equal(1, excluded.GetProperty("list").GetProperty("excludedCount").GetInt32());

        var tried = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/try",
            new { text = "spam and eggs", target = "discordMessage", includeAi = false }, cookie, Ct));
        Assert.Equal("spam", Assert.Single(tried.GetProperty("matches").EnumerateArray()).GetProperty("matched").GetString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<ApiTestHost> StartAsync(
        FakeDiscordActions? discord = null, FakeAi? ai = null, Action<IServiceCollection>? configure = null)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using (var db = _db.NewContext())
        {
            await db.ModerationFlags.ExecuteDeleteAsync(Ct);
            await db.ModerationTermLists.ExecuteDeleteAsync(Ct);
            await db.ModerationTopics.ExecuteDeleteAsync(Ct);
            await db.AiUsage.ExecuteDeleteAsync(Ct);
            await db.AiFeatureLimits.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (discord is not null)
                services.AddSingleton<IDiscordModerationActions>(discord);
            if (ai is not null)
                services.AddScoped<IAiClients>(_ => ai);
            configure?.Invoke(services);
        });
    }

    private async Task SwitchOnAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true, dailyAiCallLimit = 200 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ModerationOutcome> CheckAsync(ApiTestHost host, DiscordMessageToCheck message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationChecker>().CheckDiscordMessageAsync(message, Ct);
    }

    private static DiscordMessageToCheck Message(string id, string author, string text)
        => new(Guild, Channel, id, author, author, text);

    private static object Term(string kind, string text) => new { id = (string?)null, kind, text };

    private static object List(
        string name, object[] terms, string[]? targets = null, bool delete = false, int? timeout = null) => new
        {
            name,
            enabled = true,
            targets = targets ?? ["discordMessage"],
            deleteMessage = delete,
            timeoutMinutes = timeout,
            terms,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }

    public sealed class FakeDiscordActions : IDiscordModerationActions
    {
        public List<(string ChannelId, string MessageId)> Deleted { get; } = [];

        public List<(string GuildId, string UserId, TimeSpan Duration)> TimedOut { get; } = [];

        public Task<DiscordActionOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
        {
            Deleted.Add((channelId, messageId));
            return Task.FromResult(DiscordActionOutcome.Ok);
        }

        public Task<DiscordActionOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
        {
            TimedOut.Add((guildId, userId, duration));
            return Task.FromResult(DiscordActionOutcome.Ok);
        }
    }

    /// <summary>An AI endpoint that answers every chat completion from a script.</summary>
    public sealed class FakeAi(Func<string, string> reply) : IAiClients
    {
        public Task<AiChat?> GetChatAsync(CancellationToken ct)
        {
            var handler = new Handler(request =>
            {
                var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return Json(JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-1",
                    @object = "chat.completion",
                    created = 1_700_000_000,
                    model = "m",
                    choices = new[]
                    {
                        new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = reply(body) } },
                    },
                    usage = new
                    {
                        prompt_tokens = 120,
                        completion_tokens = 30,
                        total_tokens = 150,
                        prompt_tokens_details = new { cached_tokens = 100 },
                    },
                }));
            });

            var client = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://llm.example/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            });

            return Task.FromResult<AiChat?>(new AiChat(client.GetChatClient("m"), client, "m", "custom"));
        }

        public Task<AiTestResult> TestAsync(AiConnection connection, CancellationToken ct) => throw new NotSupportedException();

        public Task<AiModelList> ListModelsAsync(AiConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }
}
