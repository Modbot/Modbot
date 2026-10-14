using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Supporters;

/// <summary>
/// Taking in a Ko-fi payment: only monthly payments count, a repeat counts once, a late payment does
/// not undo a newer tier, a payment can make its own tier, and a new tier is thanked again.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupporterIntakeTests
{
    private readonly PostgresFixture _db;

    public SupporterIntakeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KofiPayment Payment(
        string code,
        string tier = "Gold",
        string? discord = null,
        string email = "sam@example.com",
        DateTimeOffset? at = null,
        bool monthly = true,
        string? message = null)
        => new()
        {
            Message = message,
            TransactionId = code,
            TierName = tier,
            FromName = "Sam",
            Email = email,
            IsSubscriptionPayment = monthly,
            Timestamp = at?.ToString("o"),
            DiscordUserIdRaw = discord is null ? null : System.Text.Json.JsonDocument.Parse($"\"{discord}\"").RootElement,
        };

    private static async Task<SupporterIntakeOutcome> TakeAsync(TestServices services, KofiPayment payment)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SupporterIntake>()
            .TakeKofiAsync(payment, services.Clock.UtcNow, Ct);
    }

    [Fact]
    public async Task TwoDifferentPaymentsFromTheSameNewPayerAtOnceAreBothCounted()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        var outcomes = await Task.WhenAll(
            TakeAsync(services, Payment("one", discord: "5001")),
            TakeAsync(services, Payment("two", discord: "5001")));

        Assert.All(outcomes, o => Assert.Equal(SupporterIntake.Taken, o.Result));
        await using var db = services.Database.NewContext();
        Assert.Single(await db.Supporters.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task APaymentThatIsNotMonthlyIsIgnored()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        var outcome = await TakeAsync(services, Payment("one", monthly: false));

        Assert.Equal(SupporterIntake.Ignored, outcome.Result);
        await using var db = services.Database.NewContext();
        Assert.Empty(await db.Supporters.ToListAsync(Ct));
        Assert.Empty(await db.SupporterTiers.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheFirstPaymentMakesTheSupporterAndTheTier_WithNoRolesYet()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        var outcome = await TakeAsync(services, Payment("one", discord: "5001"));

        Assert.Equal(SupporterIntake.Taken, outcome.Result);
        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        var tier = await db.SupporterTiers.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("5001", supporter.DiscordUserId);
        Assert.Equal(tier.Id, supporter.TierId);
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Null(tier.DiscordRoleId);
        Assert.Null(tier.VRChatRoleId);
        Assert.DoesNotContain("sam@example.com", supporter.PayerId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameMessageTwiceCountsOnce()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        await TakeAsync(services, Payment("one"));
        var again = await TakeAsync(services, Payment("one"));

        Assert.Equal(SupporterIntake.Repeat, again.Result);
        await using var db = services.Database.NewContext();
        Assert.Single(await db.SupporterPayments.ToListAsync(Ct));
        Assert.Single(await db.Supporters.ToListAsync(Ct));
    }

    [Fact]
    public async Task ALaterPaymentFromTheSamePersonMovesThePaidThroughDateOn()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", at: now.AddDays(-40)));
        await TakeAsync(services, Payment("two", at: now.AddDays(-1)));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
        Assert.Equal(SupporterStates.Active, supporter.State);
        Assert.Equal(SupporterRules.PaidThrough(now.AddDays(-1)), supporter.PaidThrough);
    }

    [Theory]
    [InlineData(3, SupporterStates.Ended)]
    [InlineData(0, SupporterStates.Ended)]
    [InlineData(10, SupporterStates.Active)]
    [InlineData(SupporterLimits.RolesNeverOff, SupporterStates.Ended)]
    public async Task APaymentWhoseMonthRanOutAFewDaysAgoIsEndedOrNotBySettingsDays(int days, string expected)
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.KofiRolesOffDays = days, Ct);
        var now = services.Clock.UtcNow;

        // Paid 35 days ago: the month ran out between four and seven days ago.
        await TakeAsync(services, Payment("one", at: now.AddDays(-35)));

        await using var db = services.Database.NewContext();
        Assert.Equal(expected, (await db.Supporters.AsNoTracking().SingleAsync(Ct)).State);
    }

    [Fact]
    public async Task ALatePaymentDoesNotUndoANewerTier()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("new", tier: "Platinum", at: now.AddDays(-1)));
        await TakeAsync(services, Payment("old", tier: "Gold", at: now.AddDays(-20)));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        var tier = await db.SupporterTiers.AsNoTracking().SingleAsync(t => t.Id == supporter.TierId, Ct);
        Assert.Equal("Platinum", tier.Name);
        Assert.Equal(SupporterRules.PaidThrough(now.AddDays(-1)), supporter.PaidThrough);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task ANewTierIsThankedAgain()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", tier: "Gold", at: now.AddDays(-20)));

        await using (var db = services.Database.NewContext())
        {
            await db.Supporters.ExecuteUpdateAsync(u => u.SetProperty(s => s.ThanksSentAt, now), Ct);
        }

        await TakeAsync(services, Payment("two", tier: "Platinum", at: now.AddDays(-1)));

        await using var read = services.Database.NewContext();
        Assert.Null((await read.Supporters.AsNoTracking().SingleAsync(Ct)).ThanksSentAt);
    }

    [Fact]
    public async Task TheSameTierAgainIsNotThankedAgain()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", at: now.AddDays(-20)));

        await using (var db = services.Database.NewContext())
        {
            await db.Supporters.ExecuteUpdateAsync(u => u.SetProperty(s => s.ThanksSentAt, now), Ct);
        }

        await TakeAsync(services, Payment("two", at: now.AddDays(-1)));

        await using var read = services.Database.NewContext();
        Assert.NotNull((await read.Supporters.AsNoTracking().SingleAsync(Ct)).ThanksSentAt);
    }

    // ── The code from /supporter-code in the message ───────────────────────────────────────

    private static async Task<string> CodeForAsync(TestServices services, string discordUserId)
    {
        await using var db = services.Database.NewContext();
        return await new SupporterCodes(db).ForAsync(discordUserId, services.Clock.UtcNow, Ct);
    }

    [Fact]
    public async Task APaymentWithNoDiscordLink_ButTheCodeInItsMessage_IsMatchedToTheCodesAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var code = await CodeForAsync(services, "7001");

        var outcome = await TakeAsync(services, Payment("one", message: $"Thanks for the streams! {code.ToLowerInvariant()}"));

        Assert.Equal(SupporterIntake.Taken, outcome.Result);
        await using var db = services.Database.NewContext();
        Assert.Equal("7001", (await db.Supporters.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task ALaterPaymentWithNoCode_StillFindsTheSameSupporter_AndTheirAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var code = await CodeForAsync(services, "7001");
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", at: now.AddDays(-40), message: code));
        await TakeAsync(services, Payment("two", at: now.AddDays(-10), message: null));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("7001", supporter.DiscordUserId);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AKofiDiscordLink_WinsOverACodeInTheMessage()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var code = await CodeForAsync(services, "7001");

        await TakeAsync(services, Payment("one", discord: "7002", message: code));

        await using var db = services.Database.NewContext();
        Assert.Equal("7002", (await db.Supporters.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Keep up the good work")]
    [InlineData("ZZZZZZ")]
    public async Task AMessageWithNoKnownCode_LeavesTheSupporterNotMatched(string? message)
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await CodeForAsync(services, "7001");

        var outcome = await TakeAsync(services, Payment("one", message: message));

        Assert.Equal(SupporterIntake.Taken, outcome.Result);
        await using var db = services.Database.NewContext();
        Assert.Null((await db.Supporters.AsNoTracking().SingleAsync(Ct)).DiscordUserId);
    }

    [Fact]
    public async Task AMatchedSupporterPayingAgainWithSomeoneElsesCode_StaysOnTheirOwnAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var own = await CodeForAsync(services, "7001");
        var someoneElses = await CodeForAsync(services, "7002");
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", at: now.AddDays(-40), message: own));
        await TakeAsync(services, Payment("two", at: now.AddDays(-10), message: someoneElses));

        await using var db = services.Database.NewContext();
        var supporter = await db.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("7001", supporter.DiscordUserId);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task ACodeForAnAccountThatAlreadyHasASupporter_IsNotApplied()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var code = await CodeForAsync(services, "7001");

        await TakeAsync(services, Payment("one", email: "first@example.com", message: code));
        await TakeAsync(services, Payment("two", email: "second@example.com", message: code));

        await using var db = services.Database.NewContext();
        var supporters = await db.Supporters.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(2, supporters.Count);
        Assert.Equal("7001", Assert.Single(supporters, s => s.DiscordUserId != null).DiscordUserId);
        Assert.Equal(2, await db.SupporterPayments.CountAsync(Ct));
    }

    [Fact]
    public async Task AKofiLinkThatMovesASupporterToAnotherAccount_RemembersWhoseRoleIsStillOut()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", discord: "7001", at: now.AddDays(-40)));

        // Modbot gave the first account a role (the role pass keeps the account beside it, and does not run here).
        await using (var db = services.Database.NewContext())
        {
            var given = await db.Supporters.SingleAsync(Ct);
            given.GivenDiscordRoleId = "9001";
            await db.SaveChangesAsync(Ct);
        }

        await TakeAsync(services, Payment("two", discord: "7002", at: now.AddDays(-10)));

        await using var read = services.Database.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("7002", supporter.DiscordUserId);
        Assert.Equal("7001", supporter.GivenDiscordUserId);
        Assert.Equal("9001", supporter.GivenDiscordRoleId);
    }

    [Fact]
    public async Task AKofiLinkThatMovesASupporterWhoHasNoRoleOut_RemembersNoAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await TakeAsync(services, Payment("one", discord: "7001", at: now.AddDays(-40)));
        await TakeAsync(services, Payment("two", discord: "7002", at: now.AddDays(-10)));

        await using var read = services.Database.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("7002", supporter.DiscordUserId);
        Assert.Null(supporter.GivenDiscordUserId);
    }

    [Fact]
    public async Task TwoSupportersCannotHaveTheSameDiscordAccount_ButManyCanHaveNone()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await using var db = services.Database.NewContext();

        Supporter Make(string payer, string? account) => new()
        {
            PayerId = payer,
            Name = payer,
            DiscordUserId = account,
            FirstPaidAt = services.Clock.UtcNow,
            LastPaidAt = services.Clock.UtcNow,
            PaidThrough = services.Clock.UtcNow.AddMonths(1),
            UpdatedAt = services.Clock.UtcNow,
        };

        db.Supporters.AddRange(Make("none-one", null), Make("none-two", null), Make("has", "7001"));
        await db.SaveChangesAsync(Ct);

        db.Supporters.Add(Make("same", "7001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
