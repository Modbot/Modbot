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
        bool monthly = true)
        => new()
        {
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
}
