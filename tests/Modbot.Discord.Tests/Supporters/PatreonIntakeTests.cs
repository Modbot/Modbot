using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Supporters;

/// <summary>
/// Taking in a Patreon membership message: an active patron gets the tier's roles, a declined charge
/// keeps them three days, a pledge that ended keeps them until the paid month is up, the highest tier
/// is counted, and a charge is written down once without hiding a later message about the same charge.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PatreonIntakeTests
{
    private const string Member = "member-1";
    private static readonly DateTimeOffset Charged = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public PatreonIntakeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PatreonMessage Message(
        string status = "active_patron",
        string[]? tiers = null,
        DateTimeOffset? charge = null,
        string chargeStatus = "Paid",
        string member = Member,
        string name = "Sam Patron")
    {
        tiers ??= ["gold"];

        var tierRefs = string.Join(",", tiers.Select(t => "{\"id\":\"" + t + "\"}"));
        var tierBodies = string.Join(",", new[]
        {
            """{"id":"bronze","type":"tier","attributes":{"title":"Bronze","amount_cents":300}}""",
            """{"id":"silver","type":"tier","attributes":{"title":"Silver","amount_cents":500}}""",
            """{"id":"gold","type":"tier","attributes":{"title":"Gold","amount_cents":1500}}""",
        });
        var chargeJson = charge is null
            ? ""
            : ",\"last_charge_date\":\"" + charge.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                + "\",\"last_charge_status\":\"" + chargeStatus + "\"";

        var json = """
            {"data":{"id":"@MEMBER@","type":"member","attributes":{"patron_status":"@STATUS@","full_name":"@NAME@","email":"sam@example.com"@CHARGE@},
              "relationships":{"currently_entitled_tiers":{"data":[@TIERS@]}}},
             "included":[@BODIES@]}
            """
            .Replace("@MEMBER@", member, StringComparison.Ordinal)
            .Replace("@STATUS@", status, StringComparison.Ordinal)
            .Replace("@NAME@", name, StringComparison.Ordinal)
            .Replace("@CHARGE@", chargeJson, StringComparison.Ordinal)
            .Replace("@TIERS@", tierRefs, StringComparison.Ordinal)
            .Replace("@BODIES@", tierBodies, StringComparison.Ordinal);

        return PatreonMessage.Parse(System.Text.Encoding.UTF8.GetBytes(json)) ?? throw new InvalidOperationException("Not a message.");
    }

    private static async Task<SupporterIntakeOutcome> TakeAsync(TestServices services, PatreonMessage message, string? trigger = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SupporterIntake>()
            .TakePatreonAsync(message, trigger, services.Clock.UtcNow, Ct);
    }

    [Fact]
    public async Task AnActivePatronBecomesAnActiveSupporterPaidForAMonth_WaitingToBeMatched()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        var outcome = await TakeAsync(services, Message(charge: Charged), "members:pledge:create");

        Assert.Equal(SupporterIntake.Taken, outcome.Result);
        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterProviders.Patreon, supporter.Provider);
        Assert.Equal("Sam Patron", supporter.Name);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Null(supporter.DiscordUserId);
        Assert.Null(supporter.ThanksSentAt);
        Assert.Equal(Charged, supporter.LastPaidAt);
        Assert.Equal(Charged.AddMonths(1), supporter.PaidThrough);

        var tier = await db.SupporterTiers.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterProviders.Patreon, tier.Provider);
        Assert.Equal("gold", tier.Key);
        Assert.Equal("Gold", tier.Name);
        Assert.Null(tier.DiscordRoleId);
        Assert.Equal(tier.Id, supporter.TierId);
    }

    [Fact]
    public async Task TheEmailAddressIsNotKept()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        await TakeAsync(services, Message(charge: Charged));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.DoesNotContain("example.com", supporter.PayerId + supporter.Name);
    }

    [Fact]
    public async Task OfSeveralEntitledTiers_TheOneThatCostsTheMostIsTheSupportersTier()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        await TakeAsync(services, Message(tiers: ["bronze", "gold", "silver"], charge: Charged));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        var tier = await db.SupporterTiers.AsNoTracking().SingleAsync(t => t.Id == supporter.TierId, Ct);
        Assert.Equal("gold", tier.Key);
    }

    [Fact]
    public async Task APatronWithNoTier_AndNoSupporterRow_IsIgnored()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        var outcome = await TakeAsync(services, Message(tiers: [], charge: Charged), "members:create");

        Assert.Equal(SupporterIntake.Ignored, outcome.Result);
        await using var db = services.Database.NewContext();
        Assert.Empty(await db.Supporters.ToListAsync(Ct));
        Assert.Empty(await db.SupporterTiers.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("declined_patron")]
    [InlineData("former_patron")]
    public async Task APatronWhoNeverCounted_ThenDeclinedOrGone_IsIgnored(string status)
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        var outcome = await TakeAsync(services, Message(status, tiers: [], charge: Charged, chargeStatus: status == "declined_patron" ? "Declined" : "Paid"));

        Assert.Equal(SupporterIntake.Ignored, outcome.Result);
        await using var db = services.Database.NewContext();
        Assert.Empty(await db.Supporters.ToListAsync(Ct));
    }

    [Fact]
    public async Task AMessageWithNoMemberOrNoStatus_IsIgnored()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var noStatus = PatreonMessage.Parse("""{"data":{"id":"m","attributes":{"full_name":"Free"}}}"""u8) ?? throw new InvalidOperationException();
        var noMember = PatreonMessage.Parse("""{"data":{"attributes":{"patron_status":"active_patron"}}}"""u8) ?? throw new InvalidOperationException();

        Assert.Equal(SupporterIntake.Ignored, (await TakeAsync(services, noStatus)).Result);
        Assert.Equal(SupporterIntake.Ignored, (await TakeAsync(services, noMember)).Result);
    }

    [Fact]
    public async Task ThePledgeDeleteMessage_AfterTheChargeMessage_IsNotMistakenForARepeat_AndTheMonthIsKept()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged), "members:update");

        services.Clock.UtcNow = Charged.AddDays(10);
        var outcome = await TakeAsync(services, Message("former_patron", tiers: [], charge: Charged), "members:pledge:delete");

        Assert.Equal(SupporterIntake.Repeat, outcome.Result);
        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Charged.AddMonths(1), supporter.PaidThrough);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Equal(Charged.AddMonths(1).AddDays(SupporterLimits.GraceDays), SupporterRules.RolesEndAt(supporter.PaidThrough));
        Assert.Equal(1, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AfterAPledgeEnds_TheRolesEndThreeDaysAfterThePaidMonthIsUp()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged));
        services.Clock.UtcNow = Charged.AddDays(10);
        await TakeAsync(services, Message("former_patron", tiers: [], charge: Charged), "members:pledge:delete");

        // The pass compares the paid month with the clock; a supporter is lapsed three days after it is up.
        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.False(SupporterRules.HasLapsed(supporter.PaidThrough, Charged.AddMonths(1).AddDays(2)));
        Assert.True(SupporterRules.HasLapsed(supporter.PaidThrough, Charged.AddMonths(1).AddDays(3)));
    }

    [Fact]
    public async Task APledgeDeletedWithNoPaidChargeBehindIt_HasNoMonthToWaitOut()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged;
        await TakeAsync(services, Message(), "members:pledge:create");

        services.Clock.UtcNow = Charged.AddDays(1);
        await TakeAsync(services, Message("former_patron", tiers: []), "members:pledge:delete");

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Charged.AddDays(1), supporter.PaidThrough);
        Assert.Equal(0, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task ADeclinedCharge_KeepsTheRolesUntilThreeDaysAfterIt()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged));

        var declinedAt = Charged.AddMonths(1).AddHours(4);
        services.Clock.UtcNow = declinedAt.AddHours(1);
        var outcome = await TakeAsync(services, Message("declined_patron", charge: declinedAt, chargeStatus: "Declined"), "members:update");

        Assert.Equal(SupporterIntake.Taken, outcome.Result);
        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Equal(declinedAt.AddDays(SupporterLimits.GraceDays), SupporterRules.RolesEndAt(supporter.PaidThrough));
        Assert.Equal(1, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task ADeclinedChargeSeenAfterTheGraceHasPassed_EndsTheSupporter()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged));

        var declinedAt = Charged.AddMonths(1);
        services.Clock.UtcNow = declinedAt.AddDays(SupporterLimits.GraceDays + 1);
        await TakeAsync(services, Message("declined_patron", charge: declinedAt, chargeStatus: "Declined"));

        await using var db = services.Database.NewContext();
        Assert.Equal(SupporterStates.Ended, (await db.Supporters.AsNoTracking().SingleAsync(Ct)).State);
    }

    [Fact]
    public async Task ALaterMessageThatSaysActive_GivesTheRolesBack_AndThanksAgain()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged));

        var declinedAt = Charged.AddMonths(1);
        services.Clock.UtcNow = declinedAt.AddDays(SupporterLimits.GraceDays + 1);
        await TakeAsync(services, Message("declined_patron", charge: declinedAt, chargeStatus: "Declined"));

        await using (var db = services.Database.NewContext())
        {
            var ended = await db.Supporters.SingleAsync(Ct);
            ended.ThanksSentAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var paidAgain = declinedAt.AddDays(5);
        services.Clock.UtcNow = paidAgain.AddHours(1);
        await TakeAsync(services, Message(charge: paidAgain), "members:update");

        await using var read = services.Database.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Equal(paidAgain.AddMonths(1), supporter.PaidThrough);
        Assert.Null(supporter.ThanksSentAt);
        Assert.Equal(2, await read.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task APatronPatreonSaysIsActive_KeepsTheRoles_EvenWhenTheLastChargeIsOldOrNotPaidYet()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddMonths(3);

        // The last paid charge is long over (a plan that bills now and then): Patreon still says active.
        await TakeAsync(services, Message(charge: Charged), "members:update");

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.False(SupporterRules.HasLapsed(supporter.PaidThrough, services.Clock.UtcNow));
    }

    [Fact]
    public async Task TheSameChargeMessageTwice_IsOnePayment_AndTheSecondChangesNothing()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        var first = await TakeAsync(services, Message(charge: Charged), "members:update");
        services.Clock.UtcNow = Charged.AddDays(3);
        var second = await TakeAsync(services, Message(charge: Charged), "members:update");

        Assert.Equal(SupporterIntake.Taken, first.Result);
        Assert.Equal(SupporterIntake.Repeat, second.Result);
        await using var db = services.Database.NewContext();
        Assert.Equal(1, await db.SupporterPayments.CountAsync(Ct));
        var payment = await db.SupporterPayments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterProviders.Patreon, payment.Provider);
        Assert.Equal("member-1|2026-01-01T00:00:00Z", payment.Code);
        Assert.Equal(Charged.AddDays(2), (await db.Supporters.AsNoTracking().SingleAsync(Ct)).UpdatedAt);
    }

    [Fact]
    public async Task TheSameChargeMessageTwiceAtOnce_IsOnePayment()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        await Task.WhenAll(
            TakeAsync(services, Message(charge: Charged)),
            TakeAsync(services, Message(charge: Charged)));

        await using var db = services.Database.NewContext();
        Assert.Single(await db.Supporters.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(1, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AMonthlyChargeAfterAnother_IsASecondPayment_AndMovesTheMonthOn()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(charge: Charged));

        var next = Charged.AddMonths(1);
        services.Clock.UtcNow = next.AddHours(1);
        await TakeAsync(services, Message(charge: next), "members:update");

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(next, supporter.LastPaidAt);
        Assert.Equal(next.AddMonths(1), supporter.PaidThrough);
        Assert.Equal(Charged, supporter.FirstPaidAt);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AChangeOfTier_MovesTheSupporter_AndIsThankedAgain()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);
        await TakeAsync(services, Message(tiers: ["bronze"], charge: Charged));

        await using (var db = services.Database.NewContext())
        {
            var supporter = await db.Supporters.SingleAsync(Ct);
            supporter.ThanksSentAt = services.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        services.Clock.UtcNow = Charged.AddDays(5);
        await TakeAsync(services, Message(tiers: ["silver"], charge: Charged), "members:pledge:update");

        await using var read = services.Database.NewContext();
        var after = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        var tier = await read.SupporterTiers.AsNoTracking().SingleAsync(t => t.Id == after.TierId, Ct);
        Assert.Equal("silver", tier.Key);
        Assert.Null(after.ThanksSentAt);
        Assert.Equal(2, await read.SupporterTiers.CountAsync(Ct));
    }

    [Fact]
    public async Task APatreonTierAndAKofiTierWithTheSameKey_AreTwoTiers_AndPatronsStayApartFromKofiPayers()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        services.Clock.UtcNow = Charged.AddDays(2);

        using (var scope = services.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<SupporterIntake>().TakeKofiAsync(
                new KofiPayment { TransactionId = "tx-1", TierName = "gold", FromName = "Sam", Email = "sam@example.com", IsSubscriptionPayment = true },
                services.Clock.UtcNow,
                Ct);
        }

        await TakeAsync(services, Message(charge: Charged));

        await using var db = services.Database.NewContext();
        Assert.Equal(2, await db.Supporters.CountAsync(Ct));
        Assert.Equal(2, await db.SupporterTiers.CountAsync(Ct));
    }
}
