using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.AI.Usage;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Insights;

/// <summary>Writing one insight through a scripted model endpoint, and what is stored.</summary>
[Collection(nameof(PostgresCollection))]
public class InsightWriterTests : InsightTestBase
{
    public InsightWriterTests(PostgresFixture fixture) : base(fixture) { }

    private static readonly DateOnly Today = new(2029, 3, 15);

    [Fact]
    public async Task TheModelIsGivenTheFigures_AndTheTextIsStoredBesideThem()
    {
        await TurnAiOnAsync();
        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.MembersJoined, 12);

        InsightAttempt attempt;
        await using (var context = NewContext())
            attempt = await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, Today, InsightStart.Schedule("555"), Ct);

        Assert.NotNull(attempt.Insight);
        Assert.Null(attempt.NotAsked);

        var request = Assert.Single(Model.Requests);
        Assert.Equal(Endpoint + "/chat/completions", request.Url);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(BaseModel, body.RootElement.GetProperty("model").GetString());

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());

        var given = InsightFigures.FromJson(messages[1].GetProperty("content").GetString());
        Assert.NotNull(given);
        Assert.Contains(given.Figures, f => f is { Name: "Joined", Now: 12 });

        await using var read = NewContext();
        var stored = await read.Insights.AsNoTracking().SingleAsync(Ct);

        Assert.Equal("Joins were up on last week.", stored.Text);
        Assert.Null(stored.Error);
        Assert.Equal(new DateOnly(2029, 3, 8), stored.FirstDay);
        Assert.Equal(new DateOnly(2029, 3, 14), stored.LastDay);
        Assert.Equal(InsightKinds.StartedBySchedule, stored.StartedBy);
        Assert.Equal("555", stored.DiscordChannelId);
        Assert.Equal(Start, stored.CreatedAt);
        Assert.Equal("custom", stored.Provider);

        // Stored figures are the figures sent, so a reader can check the text against them.
        Assert.Equal(given.ToJson(), InsightFigures.FromJson(stored.Figures)!.ToJson());
    }

    /// <summary>M8 §6 and design §1.1: the model is never told who anybody is.</summary>
    [Fact]
    public async Task NoPersonsIdReachesTheModel()
    {
        await TurnAiOnAsync();
        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.ModeratorBans, 3, "vrchat:usr_7f3c-moderator");
        await AddTotalAsync(Day(3, 10), DailyTotalMetrics.ModeratorWarns, 1, "discord:998877665544");

        await using (var context = NewContext())
        {
            foreach (var kind in InsightKinds.All)
                await NewWriter(context).WriteAsync(kind, InsightKinds.EveryWeek, Today, InsightStart.Schedule(null), Ct);
        }

        Assert.Equal(3, Model.Requests.Count);
        Assert.All(Model.Requests, r =>
        {
            Assert.DoesNotContain("usr_7f3c", r.Body!, StringComparison.Ordinal);
            Assert.DoesNotContain("998877665544", r.Body!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TheInsightModelIsUsedWhenOneIsSet()
    {
        await TurnAiOnAsync();
        await using (var seed = NewContext())
        {
            seed.InsightSettings.Add(new InsightSettings { Model = "insight-model" });
            await seed.SaveChangesAsync(Ct);
        }

        Answer = _ => Completion("Quiet week.", model: "insight-model");

        await using var context = NewContext();
        var insight = (await NewWriter(context).WriteAsync(InsightKinds.Team, InsightKinds.EveryDay, Today, InsightStart.Schedule(null), Ct)).Insight;

        using var body = JsonDocument.Parse(Assert.Single(Model.Requests).Body!);
        Assert.Equal("insight-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("insight-model", insight!.Model);
    }

    [Fact]
    public async Task AFailedCallIsStoredWithTheProvidersError_AndIsNeverPosted()
    {
        await TurnAiOnAsync();
        Answer = _ => Refused("Unknown model base-model");

        await using (var context = NewContext())
            await NewWriter(context).WriteAsync(InsightKinds.Rooms, InsightKinds.EveryWeek, Today, InsightStart.Schedule("555"), Ct);

        await using var read = NewContext();
        var stored = await read.Insights.AsNoTracking().SingleAsync(Ct);

        Assert.Null(stored.Text);
        Assert.Equal("llm.test answered 400: Unknown model base-model", stored.Error);
        Assert.Null(stored.DiscordChannelId);
    }

    [Fact]
    public async Task WithAiOff_NothingIsAskedAndNothingIsStored()
    {
        await TurnAiOnAsync(on: false);

        await using (var context = NewContext())
        {
            var attempt = await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, Today, InsightStart.Schedule(null), Ct);
            Assert.Null(attempt.Insight);
            Assert.Equal(InsightAttempt.AiOff, attempt.NotAsked);
        }

        Assert.Empty(Model.Requests);

        await using var read = NewContext();
        Assert.False(await read.Insights.AnyAsync(Ct));
    }

    /// <summary>Design §6: every call is recorded under insights, with a person only for the button.</summary>
    [Fact]
    public async Task EachCallRecordsItsTokens_WithThePersonOnlyWhenSomebodyPressedTheButton()
    {
        await TurnAiOnAsync();
        var person = Guid.NewGuid();

        await using (var context = NewContext())
        {
            await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, Today, InsightStart.Schedule(null), Ct);
            await NewWriter(context).WriteAsync(InsightKinds.Rooms, InsightKinds.EveryWeek, Today, InsightStart.Button(person, "sam"), Ct);
        }

        await using var read = NewContext();
        var rows = await read.AiUsage.AsNoTracking().OrderBy(u => u.Id).ToListAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, u =>
        {
            Assert.Equal(AiFeatures.Insights, u.Feature);
            Assert.Equal(BaseModel, u.Model);
            Assert.Equal("custom", u.Provider);
            Assert.Equal(900, u.InputTokens);
            Assert.Equal(300, u.CachedInputTokens);
            Assert.Equal(120, u.OutputTokens);
            Assert.Equal(Start, u.At);
        });
        Assert.Null(rows[0].UserId);
        Assert.Equal(person, rows[1].UserId);
    }

    [Fact]
    public async Task ACallTheProviderRefusedRecordsNoUsage()
    {
        await TurnAiOnAsync();
        Answer = _ => Refused("No credit left");

        await using (var context = NewContext())
            await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, Today, InsightStart.Schedule(null), Ct);

        await using var read = NewContext();
        Assert.False(await read.AiUsage.AnyAsync(Ct));
    }

    [Fact]
    public async Task WithTheSpendLimitReached_NothingIsAskedAndNothingIsStored()
    {
        await TurnAiOnAsync();
        await UseUpTheLimitAsync();

        await using (var context = NewContext())
        {
            var attempt = await NewWriter(context).WriteAsync(InsightKinds.Group, InsightKinds.EveryWeek, Today, InsightStart.Button(Guid.NewGuid(), "sam"), Ct);
            Assert.Null(attempt.Insight);
            Assert.Equal("The monthly AI spend limit for Insights is reached.", attempt.NotAsked);
        }

        Assert.Empty(Model.Requests);

        await using var read = NewContext();
        Assert.False(await read.Insights.AnyAsync(Ct));
    }
}
