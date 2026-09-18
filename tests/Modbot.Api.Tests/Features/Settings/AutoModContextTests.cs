using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Moderation;
using Modbot.Moderation;
using Modbot.Analytics.Messages;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// What a rule can see beyond the one message (AI moderation design §16 to §19): the messages
/// before it, the pictures with it, the language of what was checked, and a flag sent to Reviews.
/// </summary>
/// <remarks>
/// Over the real database with a fake AI endpoint, because every one of these is a question about
/// rows: what went to the model, what the flag recorded, what the rule's card counts.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AutoModContextTests
{
    private const string Path = "/api/settings/automod";
    private const string Flags = "/api/moderation-flags";

    private const string Guild = "910000000000000001";
    private const string Channel = "910000000000000002";

    private readonly PostgresFixture _db;

    public AutoModContextTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The messages before the one being checked (§16) ──────────────────────────────────────

    [Fact]
    public async Task TheMessagesBeforeGoToTheModelMarkedAsContext_AndAreStoredWithTheFlag()
    {
        var sent = new List<string>();
        var ai = new AutoModTests.FakeAi(body =>
        {
            sent.Add(body);
            return """{"matches":[{"topic":"t1","why":"Called them a clown.","quote":"such a clown"}]}""";
        });

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await TopicAsync(host, cookie, contextMessages: 5);

        await StoredMessagesAsync(host);

        var outcome = await CheckAsync(host, Message("m9", "author-1", "you are such a clown"));
        Assert.Equal(1, outcome.FlagsWritten);

        // Everything the member wrote is in one message, between the markers, with the earlier
        // ones labelled as context and the checked one labelled as the thing being judged.
        var request = Assert.Single(sent);
        Assert.Contains("Earlier messages, for context only, never judged", request, StringComparison.Ordinal);
        Assert.Contains("that shop is a scam by the way", request, StringComparison.Ordinal);
        Assert.Contains("Judge the last one alone", request, StringComparison.Ordinal);

        await using var db = _db.NewContext();
        var flag = await db.ModerationFlags.SingleAsync(Ct);
        var ids = JsonSerializer.Deserialize<List<string>>(flag.ContextMessageIds)!;
        Assert.Equal(["m7", "m8"], ids);

        // And a moderator is shown what the model saw, not just the ids.
        var list = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Flags, null, reader, Ct));
        var context = list.GetProperty("flags")[0].GetProperty("context").EnumerateArray().ToList();
        Assert.Equal(2, context.Count);
        Assert.Equal("that shop is a scam by the way", context[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AQuoteTakenFromTheContextIsRefused_SoNothingIsFlagged()
    {
        // Bob really did write it, one message earlier. It is still not what this member said.
        var ai = new AutoModTests.FakeAi(_ =>
            """{"matches":[{"topic":"t1","why":"Called the shop a scam.","quote":"that shop is a scam"}]}""");

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await TopicAsync(host, cookie, contextMessages: 5);
        await StoredMessagesAsync(host);

        var outcome = await CheckAsync(host, Message("m9", "author-1", "you are such a clown"));

        Assert.Empty(outcome.Matches);
        Assert.Equal(0, outcome.FlagsWritten);
    }

    [Fact]
    public async Task ARuleSetToNoContextSendsNone()
    {
        var sent = new List<string>();
        var ai = new AutoModTests.FakeAi(body =>
        {
            sent.Add(body);
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await TopicAsync(host, cookie, contextMessages: 0);
        await StoredMessagesAsync(host);

        await CheckAsync(host, Message("m9", "author-1", "you are such a clown"));

        var request = Assert.Single(sent);
        Assert.DoesNotContain("Earlier messages", request, StringComparison.Ordinal);
        Assert.DoesNotContain("that shop is a scam", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileCheckNeverSendsContext()
    {
        var sent = new List<string>();
        var ai = new AutoModTests.FakeAi(body =>
        {
            sent.Add(body);
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await TopicAsync(host, cookie, contextMessages: 10, targets: ["bio"]);
        await StoredMessagesAsync(host);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IModerationChecker>()
                .CheckProfileAsync(new ProfileToCheck("usr_1", null, "a perfectly ordinary bio line", null, null), Ct);
        }

        Assert.DoesNotContain("Earlier messages", Assert.Single(sent), StringComparison.Ordinal);
    }

    // ── Pictures (§17) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PicturesAreOfferedAndSentOnlyWhenTheModelReadsThem()
    {
        var sent = new List<string>();
        var ai = new AutoModTests.FakeAi(body =>
        {
            sent.Add(body);
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);

        // The catalogue says this model reads text only, so the card says pictures are unavailable.
        await CatalogAsync(reads: ["text"]);
        var textOnly = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.False(textOnly.GetProperty("picturesAvailable").GetBoolean());

        await TopicAsync(host, cookie, contextMessages: 0, checkPictures: true);
        await MessageWithPictureAsync(host);

        await CheckAsync(host, Message("m9", "author-1", "look at this"));
        Assert.DoesNotContain("image_url", Assert.Single(sent), StringComparison.Ordinal);

        // With a model that reads pictures, the same rule sends them.
        await CatalogAsync(reads: ["text", "image"]);
        var withPictures = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.True(withPictures.GetProperty("picturesAvailable").GetBoolean());

        sent.Clear();
        await CheckAsync(host, Message("m10", "author-1", "look at this too"));

        // FakeAi's provider is "custom", one of the ones that takes bytes rather than a link
        // (design §17), so the picture goes as fetched bytes -- the link itself is never repeated.
        var request = Assert.Single(sent);
        Assert.Contains("image_url", request, StringComparison.Ordinal);
        Assert.Contains("p1:", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureAtAPrivateAddressIsNeverSentAndNeverFetched()
    {
        var sent = new List<string>();
        var ai = new AutoModTests.FakeAi(body =>
        {
            sent.Add(body);
            return """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await CatalogAsync(reads: ["text", "image"]);
        await TopicAsync(host, cookie, contextMessages: 0, checkPictures: true);

        await MessageWithPictureAsync(host, """
            [{"name":"inside.png","type":"image/png","size":10,"url":"https://169.254.169.254/latest/meta-data"}]
            """);

        await CheckAsync(host, Message("m9", "author-1", "look at this"));

        var request = Assert.Single(sent);
        Assert.DoesNotContain("169.254.169.254", request, StringComparison.Ordinal);
        Assert.DoesNotContain("image_url", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureFlagSaysWhichPictureMatched()
    {
        var ai = new AutoModTests.FakeAi(_ =>
            """{"matches":[{"topic":"t1","why":"The picture is a slur.","quote":"","picture":"p1"}]}""");

        await using var host = await StartAsync(ai);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await SwitchOnAsync(host, cookie);
        await CatalogAsync(reads: ["text", "image"]);
        await TopicAsync(host, cookie, contextMessages: 0, checkPictures: true);
        await MessageWithPictureAsync(host);

        var outcome = await CheckAsync(host, Message("m9", "author-1", "look at this"));
        Assert.Equal(1, outcome.FlagsWritten);

        await using var db = _db.NewContext();
        var flag = await db.ModerationFlags.SingleAsync(Ct);
        Assert.Equal("Attachment cat.png", flag.Picture);
        Assert.Equal("https://cdn.example/cat.png", flag.PictureUrl);
        Assert.Equal("Attachment cat.png", flag.Matched);
    }

    // ── Language (§18) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryFlagRecordsItsLanguage_AndTheFlagsPageFiltersByIt()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "nitro");

        await CheckAsync(host, Message("m1", "author-1", "free nitro here, just log in with your account"));
        await CheckAsync(host, Message("m2", "author-2", "бесплатный nitro здесь, просто войдите в свою учётную запись"));

        var all = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Flags, null, reader, Ct));
        Assert.Equal(2, all.GetProperty("flags").GetArrayLength());

        var languages = all.GetProperty("languages").EnumerateArray()
            .ToDictionary(l => l.GetProperty("language").GetString() ?? "unknown", l => l.GetProperty("flags").GetInt32());
        Assert.Equal(1, languages["eng"]);
        Assert.Equal(1, languages["rus"]);

        var russian = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Flags}?language=rus", null, reader, Ct));
        var only = Assert.Single(russian.GetProperty("flags").EnumerateArray());
        Assert.Equal("author-2", only.GetProperty("subjectId").GetString());
        Assert.Equal("Russian", only.GetProperty("languageLabel").GetString());
    }

    [Fact]
    public async Task ARulesDismissalRateIsBrokenDownByLanguage()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewTickets | ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "nitro");

        await CheckAsync(host, Message("m1", "author-1", "free nitro here, just log in with your account"));
        await CheckAsync(host, Message("m2", "author-2", "бесплатный nitro здесь, просто войдите в свою учётную запись"));
        await CheckAsync(host, Message("m3", "author-3", "тут бесплатный nitro, это точно не обман, заходите"));

        // The English flag stands; both Russian ones were wrong. M8 §4.4 in one rule.
        var flags = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Flags, null, reviewer, Ct));
        foreach (var flag in flags.GetProperty("flags").EnumerateArray())
        {
            if (flag.GetProperty("language").GetString() == "rus")
            {
                var id = flag.GetProperty("id").GetGuid();
                var dismissed = await host.SendJsonAsync(HttpMethod.Post, $"{Flags}/{id}/dismiss", null, reviewer, Ct);
                Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
            }
        }

        var card = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        var stats = card.GetProperty("lists")[0].GetProperty("stats");

        Assert.Equal(3, stats.GetProperty("flags").GetInt32());
        Assert.Equal(2, stats.GetProperty("dismissed").GetInt32());

        var byLanguage = stats.GetProperty("byLanguage").EnumerateArray()
            .ToDictionary(r => r.GetProperty("language").GetString()!, r => r);

        Assert.Equal(0, byLanguage["eng"].GetProperty("dismissed").GetInt32());
        Assert.Equal(2, byLanguage["rus"].GetProperty("dismissed").GetInt32());
        Assert.Equal("Russian", byLanguage["rus"].GetProperty("label").GetString());
    }

    // ── Reviews (§19) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleCanOpenAReviewForEachFlag_AndClosingItAsWrongDismissesTheFlag()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (user, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewTickets | ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "nitro", openReviewForEachFlag: true);

        await CheckAsync(host, Message("m1", "author-1", "free nitro here, just log in with your account"));

        var reviews = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/reviews", null, reviewer, Ct));
        var review = Assert.Single(reviews.GetProperty("reviews").EnumerateArray());
        Assert.Equal("ai-flag", review.GetProperty("signal").GetString());
        Assert.Contains("Scams flagged", review.GetProperty("summary").GetString()!, StringComparison.Ordinal);

        var id = review.GetProperty("id").GetGuid();

        // A flag review has to say which way it went.
        var noOutcome = await host.SendJsonAsync(HttpMethod.Post, $"/api/reviews/{id}/close", new { note = "Looked at it." }, reviewer, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noOutcome.StatusCode);

        var closed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"/api/reviews/{id}/close",
            new { note = "The word was in a warning about the scam.", outcome = "wrong" }, reviewer, Ct));
        Assert.Equal("wrong", closed.GetProperty("outcome").GetString());

        await using (var db = _db.NewContext())
        {
            var flag = await db.ModerationFlags.SingleAsync(Ct);
            Assert.Equal(ModerationFlagState.Dismissed, flag.State);
            Assert.Equal(user.Username, flag.DismissedByUsername);
            Assert.NotNull(flag.ReviewId);
        }

        var card = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        var stats = card.GetProperty("lists")[0].GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("dismissed").GetInt32());
        Assert.Equal(0, stats.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task AFlagCanBeSentToReviews_AndClosingItAsRightConfirmsIt()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (user, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewTickets | ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "nitro");

        await CheckAsync(host, Message("m1", "author-1", "free nitro here, just log in with your account"));

        var flags = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Flags, null, reviewer, Ct));
        var flagId = flags.GetProperty("flags")[0].GetProperty("id").GetGuid();

        var opened = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Flags}/{flagId}/review", null, reviewer, Ct));
        Assert.NotEqual(Guid.Empty, opened.GetProperty("reviewId").GetGuid());

        // Once, not twice.
        var again = await host.SendJsonAsync(HttpMethod.Post, $"{Flags}/{flagId}/review", null, reviewer, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var reviewId = opened.GetProperty("reviewId").GetGuid();
        var closed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"/api/reviews/{reviewId}/close",
            new { note = "It really was a scam.", outcome = "right" }, reviewer, Ct));
        Assert.Equal("right", closed.GetProperty("outcome").GetString());

        await using (var db = _db.NewContext())
        {
            var flag = await db.ModerationFlags.SingleAsync(Ct);
            Assert.Equal(ModerationFlagState.Confirmed, flag.State);
            Assert.Equal(user.Username, flag.ConfirmedByUsername);
        }

        var fact = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AutoModFlagConfirmed, "author-1", Ct)));
        Assert.Equal("Scams", fact.GetProperty("ruleName").GetString());

        var card = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        var stats = card.GetProperty("lists")[0].GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("confirmed").GetInt32());
        Assert.Equal(0, stats.GetProperty("dismissed").GetInt32());

        var confirmed = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Flags}?state=confirmed", null, reviewer, Ct));
        Assert.Single(confirmed.GetProperty("flags").EnumerateArray());
    }

    [Fact]
    public async Task OpeningAReviewForAFlagNeedsReviewTickets()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        await SwitchOnAsync(host, cookie);
        await ListAsync(host, cookie, "Scams", "nitro");
        await CheckAsync(host, Message("m1", "author-1", "free nitro here, just log in with your account"));

        var flags = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Flags, null, reader, Ct));
        var flagId = flags.GetProperty("flags")[0].GetProperty("id").GetGuid();

        var refused = await host.SendJsonAsync(HttpMethod.Post, $"{Flags}/{flagId}/review", null, reader, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var gone = await host.SendJsonAsync(HttpMethod.Post, $"{Flags}/{Guid.NewGuid()}/review", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, gone.StatusCode);
    }

    [Fact]
    public async Task AReviewOfAModeratorsPatternStillHasNoRightOrWrong()
    {
        await using var host = await StartAsync();
        var (_, reviewer) = await host.SignedInAsync(ModbotPermissions.ReviewTickets, Ct);

        var now = host.Clock.UtcNow;
        Guid id;

        await using (var db = _db.NewContext())
        {
            var review = new Review
            {
                Id = Guid.CreateVersion7(now),
                ModeratorPlatform = FactPlatform.VRChat,
                ModeratorId = "usr_mod",
                Signal = ReviewSignal.SamePerson,
                About = "usr_them",
                WindowStart = now.AddDays(-2),
                WindowEnd = now,
                Summary = "Acted on the same person four times.",
                Evidence = """{"actions":4,"byKind":{},"factIds":[],"threshold":{}}""",
                OpenedAt = now,
                UpdatedAt = now,
            };
            db.Reviews.Add(review);
            await db.SaveChangesAsync(Ct);
            id = review.Id;
        }

        var refused = await host.SendJsonAsync(HttpMethod.Post, $"/api/reviews/{id}/close",
            new { note = "Fine.", outcome = "right" }, reviewer, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var closed = await host.SendJsonAsync(HttpMethod.Post, $"/api/reviews/{id}/close", new { note = "Fine." }, reviewer, Ct);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<ApiTestHost> StartAsync(AutoModTests.FakeAi? ai = null)
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
            await db.DiscordMessages.ExecuteDeleteAsync(Ct);
            await db.Reviews.ExecuteDeleteAsync(Ct);
            await db.AiCatalogModels.ExecuteDeleteAsync(Ct);
            await db.AiUsage.ExecuteDeleteAsync(Ct);
            await db.AiFeatureLimits.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (ai is not null)
                services.AddScoped<IAiClients>(_ => ai);

            // FakeAi answers as a "custom" provider, which is one of the ones that takes bytes
            // rather than a link (design §17), so a picture test needs something to actually
            // answer the fetch: a real request to cdn.example would only ever fail.
            services.AddHttpClient(ModerationPictures.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FakePictureHandler());
        });
    }

    /// <summary>Answers every picture fetch with a one-pixel PNG, whatever the URL.</summary>
    private sealed class FakePictureHandler : HttpMessageHandler
    {
        // A minimal but real PNG, so the byte cap and content checks in ModerationPictures see a
        // genuine image rather than a handful of arbitrary bytes.
        private static readonly byte[] OnePixelPng =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(OnePixelPng),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    /// <summary>What the model reads, as the model list last said.</summary>
    private async Task CatalogAsync(string[] reads)
    {
        await using var db = _db.NewContext();
        await db.AiCatalogModels.ExecuteDeleteAsync(Ct);
        db.AiCatalogModels.Add(new AiCatalogModel
        {
            Model = "m",
            Maker = "fake",
            InputModalities = [.. reads],
            OutputModalities = ["text"],
            FetchedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Two messages before the one being checked, in the same channel.</summary>
    private async Task StoredMessagesAsync(ApiTestHost host)
    {
        await StoreAsync(host, "m7", "Alice", "look at the avatar I just bought", minutes: -2);
        await StoreAsync(host, "m8", "Bob", "that shop is a scam by the way", minutes: -1);
    }

    private async Task MessageWithPictureAsync(ApiTestHost host, string? attachments = null)
    {
        await StoreAsync(host, "m9", "author-1", "look at this", minutes: 0, attachments:
            attachments ?? """[{"name":"cat.png","type":"image/png","size":10,"url":"https://cdn.example/cat.png"}]""");
        await StoreAsync(host, "m10", "author-1", "look at this too", minutes: 1, attachments:
            attachments ?? """[{"name":"cat.png","type":"image/png","size":10,"url":"https://cdn.example/cat.png"}]""");
    }

    private async Task StoreAsync(
        ApiTestHost host, string id, string author, string text, int minutes, string? attachments = null)
    {
        var at = host.Clock.UtcNow.AddMinutes(minutes);

        await using var db = _db.NewContext();
        await new MessagePartitionMaintainer(db).EnsureForAsync([at], Ct);

        db.DiscordMessages.Add(new DiscordMessage
        {
            MessageId = id,
            SentAt = at,
            GuildId = Guild,
            ChannelId = Channel,
            AuthorId = author,
            AuthorName = author,
            Text = text,
            Attachments = attachments ?? "[]",
            StoredAt = at,
        });

        await db.SaveChangesAsync(Ct);
    }

    private static async Task SwitchOnAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true, dailyAiCallLimit = 200 }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task TopicAsync(
        ApiTestHost host,
        string cookie,
        int contextMessages,
        bool checkPictures = false,
        string[]? targets = null)
    {
        var created = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/topics", new
        {
            name = "Insults",
            instructions = "Calling another member names.",
            sensitivity = "medium",
            enabled = true,
            targets = targets ?? ["discordMessage"],
            deleteMessage = false,
            timeoutMinutes = (int?)null,
            contextMessages,
            checkPictures,
        }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    private static async Task ListAsync(
        ApiTestHost host, string cookie, string name, string term, bool openReviewForEachFlag = false)
    {
        var created = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", new
        {
            name,
            enabled = true,
            targets = new[] { "discordMessage" },
            deleteMessage = false,
            timeoutMinutes = (int?)null,
            terms = new[] { new { id = (string?)null, kind = "word", text = term } },
            openReviewForEachFlag,
        }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

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
