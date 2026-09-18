using Microsoft.EntityFrameworkCore;
using Modbot.AI.Alerts;
using Modbot.AI.Tests.Insights;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Alerts;

/// <summary>
/// The watchers against a fake clock and a seeded fact log: firing, not firing, the minimum count,
/// sensitivity, the quiet time, and what an alert holds (AI insights design §8).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AlertCheckerTests : AlertTestBase
{
    public AlertCheckerTests(PostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ASpikeOfJoinsRaisesOneAlert_AndTheSameWindowIsNotJudgedTwice()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        // Inside the quarter-hour: the checker returns at once.
        Assert.Equal(0, await RunAtAsync(Start.AddMinutes(5)));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(AlertWatchers.VRChatJoins, alert.Watcher);
        Assert.Equal(40m, alert.Now);
        Assert.Equal(4m, alert.Normal);
        Assert.Equal(Start.AddHours(-1), alert.WindowStart);
        Assert.Equal(Start, alert.WindowEnd);
        Assert.Equal(AlertSensitivities.Normal, alert.Sensitivity);
        Assert.StartsWith("/?joinedFrom=", alert.Link, StringComparison.Ordinal);

        var figures = AlertFigures.FromJson(alert.Figures);
        Assert.NotNull(figures);
        Assert.Equal("joins", figures.Counts);
        Assert.Equal(AlertFigureReader.HistoryDays, figures.Earlier.Count);
        Assert.All(figures.Earlier, v => Assert.Equal(4m, v));
    }

    [Fact]
    public async Task AnOrdinaryHourRaisesNothing()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 5);

        Assert.Equal(0, await RunAtAsync(Start));
        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task AWatcherThatIsOffRaisesNothing()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Off);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 400);

        Assert.Equal(0, await RunAtAsync(Start));
        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task TheMinimumCountKeepsAQuietGroupQuiet()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.High);

        // Nothing has ever happened in this hour, so only the minimum count stands in the way.
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 3);
        Assert.Equal(0, await RunAtAsync(Start));

        await AddFactsAsync(InWindow().AddMinutes(1), FactType.MemberJoined, 3);
        Assert.Equal(1, await RunAtAsync(Start.AddMinutes(15)));

        Assert.Equal(6m, Assert.Single(await AlertsAsync()).Now);
    }

    [Fact]
    public async Task SensitivityDecidesWhetherItFires()
    {
        await SetWatchAsync(AlertWatchers.Flags, AlertSensitivities.Low);
        await AddHistoryAsync(FactType.AutoModFlag, 4);
        await AddFactsAsync(InWindow(), FactType.AutoModFlag, 7);

        Assert.Equal(0, await RunAtAsync(Start));

        await SetWatchAsync(AlertWatchers.Flags, AlertSensitivities.High);
        Assert.Equal(1, await RunAtAsync(Start.AddMinutes(15)));

        Assert.Equal(AlertWatchers.Flags, Assert.Single(await AlertsAsync()).Watcher);
    }

    [Fact]
    public async Task TheQuietTimeHoldsARepeatDown_UntilItGetsMuchWorse()
    {
        await SetAlertSettingsAsync(s => s.QuietHours = 6);
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        // A quarter-hour later the same joins are still in the window, and it says nothing again.
        Assert.Equal(0, await RunAtAsync(Start.AddMinutes(15)));
        Assert.Single(await AlertsAsync());

        // Twice as far from normal is much worse, and that is worth saying inside the quiet time.
        await AddFactsAsync(Start.AddMinutes(25), FactType.MemberJoined, 60);
        Assert.Equal(1, await RunAtAsync(Start.AddMinutes(30)));

        var alerts = await AlertsAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Equal(100m, alerts[1].Now);
    }

    [Fact]
    public async Task TheAlertStillGoesOutWithAiOff()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Null(alert.Text);
        Assert.Equal(40m, alert.Now);
        Assert.Empty(Model.Requests);
    }

    [Fact]
    public async Task TheAlertStillGoesOutWhenTheSpendLimitIsReached()
    {
        await TurnAiOnAsync();
        await UseUpTheLimitAsync();
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        Assert.Null(Assert.Single(await AlertsAsync()).Text);
        Assert.Empty(Model.Requests);
    }

    [Fact]
    public async Task WithAiOnTheModelWritesOneSentence_AndIsGivenCountsOnly()
    {
        Answer = _ => Completion("Forty people joined in the last hour, against about four on a usual Thursday.");

        await TurnAiOnAsync();
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal("Forty people joined in the last hour, against about four on a usual Thursday.", alert.Text);
        Assert.Equal(BaseModel, alert.Model);

        var request = Assert.Single(Model.Requests);
        Assert.DoesNotContain("usr_", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAlertWritesAFactThatNamesTheWatcherAndNobodyElse()
    {
        await SetWatchAsync(AlertWatchers.VRChatJoins, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 40);

        await RunAtAsync(Start);

        await using var context = NewContext();
        var fact = await context.Events.AsNoTracking()
            .SingleAsync(e => e.Type == FactType.InsightAlert, Ct);

        Assert.Equal(FactPlatform.Modbot, fact.SubjectPlatform);
        Assert.Equal(AlertWatchers.VRChatJoins, fact.SubjectId);
        Assert.Null(fact.ActorId);
        Assert.DoesNotContain("usr_", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDiscordChannelIsCopiedFromTheSettings()
    {
        await SetAlertSettingsAsync(s => s.DiscordChannelId = "1234567890");
        await SetWatchAsync(AlertWatchers.Leaves, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberLeft, 4);
        await AddFactsAsync(InWindow(), FactType.MemberLeft, 40);

        Assert.Equal(1, await RunAtAsync(Start));

        Assert.Equal("1234567890", Assert.Single(await AlertsAsync()).DiscordChannelId);
    }

    [Fact]
    public async Task ModerationActionsAreCountedTogetherWhateverThePlatform()
    {
        await SetWatchAsync(AlertWatchers.Actions, AlertSensitivities.Normal);
        await AddHistoryAsync(FactType.MemberBanned, 2);

        await AddFactsAsync(InWindow(), FactType.MemberBanned, 10);
        await AddFactsAsync(InWindow().AddMinutes(1), FactType.DiscordMemberTimedOut, 10);

        Assert.Equal(1, await RunAtAsync(Start));
        Assert.Equal(20m, Assert.Single(await AlertsAsync()).Now);
    }

    [Fact]
    public async Task ADropInActiveMembersIsJudgedAgainstTheFourWeeksBefore()
    {
        await SetWatchAsync(AlertWatchers.ActiveDrop, AlertSensitivities.Normal);

        // Yesterday is the last whole day, so the week just ended is the seven days before it.
        var lastWholeDay = DateOnly.FromDateTime(Start.UtcDateTime).AddDays(-1);

        for (var week = 0; week <= AlertFigureReader.HistoryWeeks; week++)
            await AddActiveAsync(lastWholeDay.AddDays(-7 * week), week == 0 ? 5 : 40, $"{week}_");

        Assert.Equal(1, await RunAtAsync(Start));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(AlertWatchers.ActiveDrop, alert.Watcher);
        Assert.Equal(5m, alert.Now);
        Assert.Equal(40m, alert.Normal);

        // Whole UTC days cannot change inside a day, so it is read once a day and not every pass.
        Assert.Equal(0, await RunAtAsync(Start.AddMinutes(15)));
        Assert.Single(await AlertsAsync());
    }

    [Fact]
    public async Task NothingIsCheckedWhileEveryWatcherIsOff()
    {
        await AddHistoryAsync(FactType.MemberJoined, 4);
        await AddFactsAsync(InWindow(), FactType.MemberJoined, 400);

        Assert.Equal(0, await RunAtAsync(Start));
        Assert.Empty(await AlertsAsync());
    }
}
