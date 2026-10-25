using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.AutomaticRoles;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.ClipRequests;
using Modbot.Api.Features.Companion.Live;
using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.AutomaticRoles;

/// <summary>The facts an automatic role rule reads: whose ban is whose, and which bans were overturned.</summary>
[Collection(nameof(PostgresCollection))]
public class ModeratorFactsGathererTests
{
    private const string ServiceAccount = "usr_modbot_service";

    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public ModeratorFactsGathererTests(PostgresFixture db) => _db = db;

    private async Task<(FakeClock Clock, ModbotUser Moderator, string Person)> StartAsync(
        ModbotPermissions permissions, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var clock = new FakeClock(Noon);

        await new EventPartitionMaintainer(db, clock).EnsureAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.VRChatAccountUserId = ServiceAccount;
        await db.SaveChangesAsync(ct);

        var moderator = await TestAccounts.CreateAsync(
            db, $"factsmod_{Guid.NewGuid():N}", TestAccounts.Password, permissions, linked: true, ct);

        return (clock, moderator, $"usr_person_{Guid.NewGuid():N}");
    }

    private async Task AddBanAsync(
        string type, FactPlatform actorPlatform, string actorId, string person, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        db.Events.Add(new ModbotEvent
        {
            Type = type,
            OccurredAt = at,
            ObservedAt = at.AddMinutes(1),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            ActorPlatform = actorPlatform,
            ActorId = actorId,
            Source = FactSource.Manual,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task AddAppealAsync(
        string person, AppealOutcome outcome, DateTimeOffset loggedAt, DateTimeOffset? decidedAt, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        db.Appeals.Add(new Appeal
        {
            UserId = person,
            ReceivedOn = DateOnly.FromDateTime(loggedAt.UtcDateTime),
            Outcome = outcome,
            LoggedAt = loggedAt,
            LoggedByUserId = Guid.NewGuid(),
            LoggedByUsername = "lead",
            DecidedAt = decidedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<ModeratorFacts> GatherAsync(FakeClock clock, Guid user, AutomaticRole rule, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var gatherer = new ModeratorFactsGatherer(
            db,
            clock,
            new DeviceLocations(),
            new CompanionConnections(),
            new ClipSettingsReports(),
            new ClipRequestStore(db));

        var facts = await gatherer.GatherAsync([rule], [user], ct);
        return Assert.Single(facts);
    }

    private static AutomaticRole ThirtyDays() => new() { OverturnedBans = true, OverturnedBansMax = 0, OverturnedBansDays = 30 };

    [Fact]
    public async Task ABanMadeInModbotCountsForThatAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);
        var at = Noon.AddHours(-2);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, at, ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Equal([at], facts.BanTimes);
        Assert.True(facts.IsModerator);
    }

    [Fact]
    public async Task ABanMadeInVRChatCountsForTheAccountThatLinkedThatId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);
        var at = Noon.AddHours(-3);

        await AddBanAsync(FactType.MemberBanned, FactPlatform.VRChat, moderator.VRChatUserId!, person, at, ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Equal([at], facts.BanTimes);
    }

    [Fact]
    public async Task ABanRecordedUnderTheServiceAccountCountsForNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);

        await AddBanAsync(FactType.MemberBanned, FactPlatform.VRChat, ServiceAccount, person, Noon.AddHours(-1), ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Empty(facts.BanTimes);
    }

    [Fact]
    public async Task ABanOlderThanADayIsKeptButIsOutsideTheDailyCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);
        var recent = Noon.AddHours(-1);
        var old = Noon.AddDays(-2);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, recent, ct);
        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, old, ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Equal(2, facts.BanTimes.Count);
        Assert.Contains(old, facts.BanTimes);

        var cap = new AutomaticRole { DailyBanCap = true, DailyBanCapMax = 1 };
        Assert.True(AutomaticRoleRule.Check(cap, facts).Holds);
    }

    [Fact]
    public async Task AnUpheldAppealCountsForTheNewestBanOfThatPersonBeforeTheAppeal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);
        var colleague = await CreateColleagueAsync(ct);
        var decided = Noon.AddDays(-1);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, Noon.AddDays(-5), ct);
        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, colleague.Id.ToString(), person, Noon.AddDays(-4), ct);
        await AddAppealAsync(person, AppealOutcome.Upheld, Noon.AddDays(-3), decided, ct);

        var mine = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);
        var theirs = await GatherAsync(clock, colleague.Id, ThirtyDays(), ct);

        Assert.Empty(mine.OverturnedDecidedAt);
        Assert.Equal([decided], theirs.OverturnedDecidedAt);
    }

    [Fact]
    public async Task ABanMadeAfterTheAppealIsNotTheOneOverturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);
        var colleague = await CreateColleagueAsync(ct);
        var decided = Noon.AddDays(-1);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, Noon.AddDays(-5), ct);
        await AddAppealAsync(person, AppealOutcome.Upheld, Noon.AddDays(-3), decided, ct);
        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, colleague.Id.ToString(), person, Noon.AddDays(-2), ct);

        var mine = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);
        var theirs = await GatherAsync(clock, colleague.Id, ThirtyDays(), ct);

        Assert.Equal([decided], mine.OverturnedDecidedAt);
        Assert.Empty(theirs.OverturnedDecidedAt);
    }

    [Theory]
    [InlineData(AppealOutcome.Pending)]
    [InlineData(AppealOutcome.TurnedDown)]
    public async Task AnAppealThatIsPendingOrTurnedDownDoesNotCount(AppealOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, Noon.AddDays(-5), ct);
        await AddAppealAsync(person, outcome, Noon.AddDays(-3), outcome == AppealOutcome.Pending ? null : Noon.AddDays(-1), ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Empty(facts.OverturnedDecidedAt);
    }

    [Fact]
    public async Task AnUpheldAppealDecidedBeforeTheLookBackDoesNotCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, moderator, person) = await StartAsync(ModbotPermissions.Ban, ct);

        await AddBanAsync(FactType.ActionBan, FactPlatform.Modbot, moderator.Id.ToString(), person, Noon.AddDays(-50), ct);
        await AddAppealAsync(person, AppealOutcome.Upheld, Noon.AddDays(-45), Noon.AddDays(-40), ct);

        var facts = await GatherAsync(clock, moderator.Id, ThirtyDays(), ct);

        Assert.Empty(facts.OverturnedDecidedAt);
    }

    [Fact]
    public async Task AnAccountWithoutAModeratorPermissionIsNotOnTheTeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, viewer, _) = await StartAsync(ModbotPermissions.None, ct);

        var facts = await GatherAsync(clock, viewer.Id, new AutomaticRole(), ct);

        Assert.False(facts.IsModerator);
        Assert.False(facts.CompanionRunning);
    }

    private async Task<ModbotUser> CreateColleagueAsync(CancellationToken ct)
    {
        await using var db = _db.NewContext();
        return await TestAccounts.CreateAsync(
            db, $"factscol_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Ban, linked: true, ct);
    }
}
