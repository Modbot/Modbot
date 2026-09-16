using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Calls;
using Modbot.AI.Moderation;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// The call log and the batching behind it, over the real database. The AI endpoint is a fake;
/// nothing leaves the process.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiCallLogTests
{
    private const string Path = "/api/settings/ai/calls";

    private readonly PostgresFixture _db;

    public AiCallLogTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole point of batching: several people's profile text in one call, with each answer
    /// going back to the person it is about.
    /// </summary>
    [Fact]
    public async Task SeveralProfilesGoInOneCall_AndEachAnswerGoesBackToItsOwnPerson()
    {
        var asked = new List<string>();

        var ai = new AiModerationTests.FakeAi(body =>
        {
            asked.Add(body);

            // The model is told which text is which; it answers about both.
            return JsonSerializer.Serialize(new
            {
                matches = new[]
                {
                    new { text = "x1", topic = "t1", why = "Asks people to vote.", quote = "vote for me" },
                    new { text = "x2", topic = "t1", why = "Asks people to vote.", quote = "vote for us" },
                },
            });
        });

        await using var host = await StartAsync(ai);
        await SwitchOnAsync(batchSize: 5);

        var outcomes = await CheckAsync(host,
            new ProfileToCheck("usr_1", null, "please vote for me", null, null),
            new ProfileToCheck("usr_2", null, "please vote for us", null, null));

        Assert.Single(asked);
        Assert.Equal(1, outcomes[0].FlagsWritten);
        Assert.Equal(1, outcomes[1].FlagsWritten);

        await using var db = _db.NewContext();
        var flags = await db.ModerationFlags.OrderBy(f => f.SubjectId).ToListAsync(Ct);
        Assert.Equal(["usr_1", "usr_2"], flags.Select(f => f.SubjectId));
        Assert.Equal("vote for me", flags[0].Matched);
        Assert.Equal("vote for us", flags[1].Matched);

        // One provider call, so one row in the log, and both flags point at it.
        var call = Assert.Single(await db.AiCalls.ToListAsync(Ct));
        Assert.Equal(AiCallOutcomes.Answered, call.Outcome);
        Assert.All(flags, f => Assert.Equal(call.Id, f.CallId));
    }

    /// <summary>
    /// A batched answer that cannot be matched back is not allowed to cost every profile in it its
    /// check: they go again one at a time, which is what Modbot did before batching existed.
    /// </summary>
    [Fact]
    public async Task AnUnreadableBatchedAnswerIsAskedAgainOneProfileAtATime()
    {
        var asked = 0;

        var ai = new AiModerationTests.FakeAi(body =>
        {
            asked++;

            // The first answer names texts nobody sent; the rest are usable.
            return asked == 1
                ? """{"matches":[{"text":"nonsense","topic":"nope","why":"","quote":""}"""
                : JsonSerializer.Serialize(new
                {
                    matches = new[] { new { text = "x1", topic = "t1", why = "Asks people to vote.", quote = "vote for me" } },
                });
        });

        await using var host = await StartAsync(ai);
        await SwitchOnAsync(batchSize: 5);

        var outcomes = await CheckAsync(host,
            new ProfileToCheck("usr_1", null, "please vote for me", null, null),
            new ProfileToCheck("usr_2", null, "please vote for me too", null, null));

        // One batched call, then one call per profile.
        Assert.Equal(3, asked);
        Assert.Equal(1, outcomes[0].FlagsWritten);
        Assert.Equal(1, outcomes[1].FlagsWritten);
    }

    /// <summary>
    /// Profile text belongs to the person it describes. A call that flagged nothing keeps counts
    /// only; a call that produced a flag keeps what the model saw, so a moderator can check it.
    /// </summary>
    [Fact]
    public async Task OnlyACallThatFlaggedKeepsWhatTheModelWasSentAndAnswered()
    {
        var asked = 0;

        var ai = new AiModerationTests.FakeAi(_ =>
        {
            asked++;
            return asked == 1
                ? JsonSerializer.Serialize(new
                {
                    matches = new[] { new { text = "x1", topic = "t1", why = "Asks people to vote.", quote = "vote for me" } },
                })
                : """{"matches":[]}""";
        });

        await using var host = await StartAsync(ai);
        await SwitchOnAsync(batchSize: 1);

        await CheckAsync(host, new ProfileToCheck("usr_1", null, "please vote for me", null, null));
        await CheckAsync(host, new ProfileToCheck("usr_2", null, "hello everyone", null, null));

        await using var db = _db.NewContext();
        var calls = await db.AiCalls.OrderBy(c => c.Id).ToListAsync(Ct);
        Assert.Equal(2, calls.Count);

        // Found by which one flagged, not by array position: the fake clock does not move
        // between the two calls, so their ids -- Guid.CreateVersion7 of the same instant --
        // are not reliably in call order.
        var flagged = Assert.Single(calls, c => c.Flagged);
        Assert.Contains("vote for me", flagged.Prompt!, StringComparison.Ordinal);
        Assert.NotNull(flagged.Answer);

        var clean = Assert.Single(calls, c => !c.Flagged);
        Assert.Null(clean.Prompt);
        Assert.Null(clean.Answer);

        // Counts are kept either way, cached input included.
        Assert.All(calls, c => Assert.Equal(120, c.InputTokens));
        Assert.All(calls, c => Assert.Equal(100, c.CachedInputTokens));
    }

    /// <summary>
    /// Counts are Modbot's own record of what it did; prompts and answers are somebody's words.
    /// The two are not the same thing to be shown, so they are not the same permission.
    /// </summary>
    [Fact]
    public async Task TheCountsNeedTheOperationalLog_AndReadingPromptsNeedsSettings()
    {
        await using var host = await StartAsync();

        var (_, operational) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var (_, plain) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Guid id;
        await using (var db = _db.NewContext())
        {
            id = Guid.CreateVersion7();
            db.AiCalls.Add(new AiCall
            {
                Id = id,
                At = DateTimeOffset.UtcNow,
                Feature = "moderation",
                ModelAsked = "m",
                Outcome = AiCallOutcomes.Answered,
                Prompt = "the instructions",
                Answer = "nothing matched",
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Path, null, operational, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, plain, Ct)).StatusCode);

        // The counts view is open to the operational log; the prompt behind one call is not.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Get, $"{Path}/{id}", null, operational, Ct)).StatusCode);

        var (_, settings) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var detail = await host.SendJsonAsync(HttpMethod.Get, $"{Path}/{id}", null, settings, Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        var body = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("the instructions", body.GetProperty("prompt").GetString());
        Assert.Equal("nothing matched", body.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task RowsPastTheKeepForSettingAreDeleted_AndZeroKeepsThemForEver()
    {
        await using var host = await StartAsync();
        Assert.NotNull(host);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        await using (var db = _db.NewContext())
        {
            db.AiCalls.AddRange(
                Row(now.AddDays(-40)),
                Row(now.AddDays(-10)),
                Row(now));

            var settings = await db.GetSettingsAsync(Ct);
            settings.AiCallLogKeepDays = 0;
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = _db.NewContext())
        {
            Assert.Equal(0, await AiCallLog.PruneAsync(db, now, Ct));
            Assert.Equal(3, await db.AiCalls.CountAsync(Ct));

            var settings = await db.GetSettingsAsync(Ct);
            settings.AiCallLogKeepDays = 30;
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = _db.NewContext())
        {
            Assert.Equal(1, await AiCallLog.PruneAsync(db, now, Ct));
            Assert.Equal(2, await db.AiCalls.CountAsync(Ct));
        }

        static AiCall Row(DateTimeOffset at) => new()
        {
            Id = Guid.CreateVersion7(at),
            At = at,
            Feature = "moderation",
            ModelAsked = "m",
            Outcome = AiCallOutcomes.Answered,
        };
    }

    private async Task<ApiTestHost> StartAsync(AiModerationTests.FakeAi? ai = null)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using (var db = _db.NewContext())
        {
            await db.AiCalls.ExecuteDeleteAsync(Ct);
            await db.ModerationFlags.ExecuteDeleteAsync(Ct);
            await db.ModerationTermLists.ExecuteDeleteAsync(Ct);
            await db.ModerationTopics.ExecuteDeleteAsync(Ct);
            await db.AiUsage.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (ai is not null)
                services.AddScoped<IAiClients>(_ => ai);
        });
    }

    /// <summary>Moderation on, one AI topic over profile bios, and the batch size under test.</summary>
    private async Task SwitchOnAsync(int batchSize)
    {
        await using var db = _db.NewContext();

        var settings = await db.GetSettingsAsync(Ct);
        settings.AiModerationEnabled = true;
        settings.AiModerationDailyCallLimit = 100;
        settings.AiModerationProfileBatchSize = batchSize;

        var now = DateTimeOffset.UtcNow;

        db.ModerationTopics.Add(new ModerationTopic
        {
            Id = Guid.CreateVersion7(now),
            Name = "Politics",
            Instructions = "Election campaigning.",
            Sensitivity = "medium",
            Enabled = true,
            Targets = (int)ModerationTargets.Bio,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<IReadOnlyList<ModerationOutcome>> CheckAsync(ApiTestHost host, params ProfileToCheck[] profiles)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationChecker>().CheckProfilesAsync(profiles, Ct);
    }
}
