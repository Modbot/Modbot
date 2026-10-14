using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;
using Modbot.Discord.Supporters;
using Modbot.Discord.Tests.Fakes;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Supporters;

/// <summary>
/// The supporter role pass: a paid-up supporter gets the tier's Discord role, and the VRChat group
/// role once their Discord account is linked to a VRChat one; both are taken back three days after
/// the paid month is up; and a role Modbot did not give is never taken.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupporterRolesTests
{
    private const string Member = "5001";
    private const string VRChatUser = "usr_supporter";

    private readonly PostgresFixture _db;

    public SupporterRolesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TestServices Services, Guid SupporterId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>A supporter who paid <paramref name="paidDaysAgo"/> days ago, on a tier giving both roles.</summary>
    private async Task<Setup> SetUpAsync(int paidDaysAgo = 1, bool linkVRChat = true, string? thanks = "Thanks {name}, {tier}!")
    {
        var services = await SyncSetUp.CreateAsync(_db, s => s.SupporterThanksText = thanks, Ct);
        var now = services.Clock.UtcNow;

        if (linkVRChat)
            await SyncSetUp.LinkAsync(services, VRChatUser, Member, ct: Ct);

        await using var db = services.Database.NewContext();

        var tier = new SupporterTier
        {
            Key = "gold",
            Name = "Gold",
            DiscordRoleId = SyncSetUp.DiscordRole,
            VRChatRoleId = SyncSetUp.GroupRole,
            CreatedAt = now,
        };
        db.SupporterTiers.Add(tier);

        var paidAt = now.AddDays(-paidDaysAgo);
        var supporter = new Supporter
        {
            PayerId = "payer",
            Name = "Sam",
            DiscordUserId = Member,
            TierId = tier.Id,
            State = SupporterStates.Active,
            FirstPaidAt = paidAt,
            LastPaidAt = paidAt,
            PaidThrough = SupporterRules.PaidThrough(paidAt),
            UpdatedAt = now,
        };
        db.Supporters.Add(supporter);

        await db.SaveChangesAsync(Ct);
        return new Setup(services, supporter.Id);
    }

    private static async Task<SupporterPass> PassAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SupporterRoles>().RunAsync(gateway, Ct);
    }

    private static async Task<Supporter> ReadAsync(Setup setup)
    {
        await using var db = setup.Services.Database.NewContext();
        return await db.Supporters.AsNoTracking().SingleAsync(s => s.Id == setup.SupporterId, Ct);
    }

    [Fact]
    public async Task APaidUpSupporterIsGivenBothRoles_AndThankedOnce()
    {
        await using var setup = await SetUpAsync();
        var gateway = new FakeGateway();

        var pass = await PassAsync(setup.Services, gateway);

        Assert.Equal(2, pass.Given);
        var (added, _, userId, roleId) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal(Member, userId);
        Assert.Equal(SyncSetUp.DiscordRole, roleId);
        Assert.Equal(("role-given", VRChatUser, SyncSetUp.GroupRole), Assert.Single(setup.Services.VRChat.Actions));
        Assert.Equal("Thanks Sam, Gold!", Assert.Single(gateway.DirectMessages).Text);

        var supporter = await ReadAsync(setup);
        Assert.Equal(SyncSetUp.DiscordRole, supporter.GivenDiscordRoleId);
        Assert.Equal(SyncSetUp.GroupRole, supporter.GivenVRChatRoleId);
        Assert.Equal(VRChatUser, supporter.GivenVRChatUserId);
        Assert.NotNull(supporter.ThanksSentAt);

        // A second pass finds nothing to do.
        var again = await PassAsync(setup.Services, gateway);
        Assert.Equal(0, again.Given + again.Taken + again.Thanked);
        Assert.Single(gateway.RoleChanges);
        Assert.Single(gateway.DirectMessages);
    }

    [Fact]
    public async Task TheRolesAreTakenBackThreeDaysAfterThePaidMonthIsUp()
    {
        await using var setup = await SetUpAsync();
        var gateway = new FakeGateway();
        await PassAsync(setup.Services, gateway);

        var through = SupporterRules.PaidThrough(setup.Services.Clock.UtcNow.AddDays(-1));

        // Two days after the month: still paid up.
        setup.Services.Clock.UtcNow = through.AddDays(2);
        await PassAsync(setup.Services, gateway);
        Assert.Equal(SupporterStates.Active, (await ReadAsync(setup)).State);
        Assert.Single(gateway.RoleChanges);

        // Three days after: both roles come off.
        setup.Services.Clock.UtcNow = through.AddDays(3);
        var pass = await PassAsync(setup.Services, gateway);

        Assert.Equal(2, pass.Taken);
        Assert.Contains(gateway.RoleChanges, c => !c.Added && c.UserId == Member && c.RoleId == SyncSetUp.DiscordRole);
        Assert.Contains(setup.Services.VRChat.Actions, a => a == ("role-taken", VRChatUser, SyncSetUp.GroupRole));

        var supporter = await ReadAsync(setup);
        Assert.Equal(SupporterStates.Ended, supporter.State);
        Assert.Null(supporter.GivenDiscordRoleId);
        Assert.Null(supporter.GivenVRChatRoleId);
    }

    [Fact]
    public async Task TheVRChatRoleFollowsTheLink()
    {
        await using var setup = await SetUpAsync(linkVRChat: false);
        var gateway = new FakeGateway();

        var first = await PassAsync(setup.Services, gateway);

        Assert.Equal(1, first.Given);
        Assert.Single(gateway.RoleChanges);
        Assert.Empty(setup.Services.VRChat.Actions);

        await SyncSetUp.LinkAsync(setup.Services, VRChatUser, Member, ct: Ct);
        var second = await PassAsync(setup.Services, gateway);

        Assert.Equal(1, second.Given);
        Assert.Single(gateway.RoleChanges);
        Assert.Equal(("role-given", VRChatUser, SyncSetUp.GroupRole), Assert.Single(setup.Services.VRChat.Actions));
    }

    [Fact]
    public async Task ARoleModbotDidNotGiveIsNeverTaken()
    {
        // Paid long ago and ended, but Modbot never gave anything: it must not remove a role somebody gave by hand.
        await using var setup = await SetUpAsync(paidDaysAgo: 90);
        var gateway = new FakeGateway();

        var pass = await PassAsync(setup.Services, gateway);

        Assert.Equal(0, pass.Taken);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(setup.Services.VRChat.Actions);
        Assert.Equal(SupporterStates.Ended, (await ReadAsync(setup)).State);
    }

    [Fact]
    public async Task ARoleThePersonAlreadyHoldsIsNotRecorded_SoItIsNeverTakenBack()
    {
        // Held already (by hand, or from Ko-fi's own Discord link): Discord's add would say Ok, but Modbot gave nothing.
        await using var setup = await SetUpAsync(linkVRChat: false);
        await SyncSetUp.LinkAsync(setup.Services, VRChatUser, Member, [SyncSetUp.GroupRole], [SyncSetUp.DiscordRole], Ct);
        var gateway = new FakeGateway();

        var pass = await PassAsync(setup.Services, gateway);

        Assert.Equal(0, pass.Given);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(setup.Services.VRChat.Actions);
        var supporter = await ReadAsync(setup);
        Assert.Null(supporter.GivenDiscordRoleId);
        Assert.Null(supporter.GivenVRChatRoleId);
        Assert.Null(supporter.GivenVRChatUserId);

        // The month runs out: nothing was given, so nothing is taken.
        setup.Services.Clock.Advance(TimeSpan.FromDays(90));
        var later = await PassAsync(setup.Services, gateway);
        Assert.Equal(0, later.Taken);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(setup.Services.VRChat.Actions);
        Assert.Equal(SupporterStates.Ended, (await ReadAsync(setup)).State);
    }

    [Fact]
    public async Task ARoleHeldByHandIsGivenOnceItIsGone()
    {
        await using var setup = await SetUpAsync(linkVRChat: false);
        await SyncSetUp.LinkAsync(setup.Services, VRChatUser, Member, [], [SyncSetUp.DiscordRole], Ct);
        var gateway = new FakeGateway();

        await PassAsync(setup.Services, gateway);
        Assert.Empty(gateway.RoleChanges);

        await SyncSetUp.SetServerRolesAsync(setup.Services, Member, [], Ct);
        await PassAsync(setup.Services, gateway);

        Assert.Equal(SyncSetUp.DiscordRole, (await ReadAsync(setup)).GivenDiscordRoleId);
        Assert.Single(gateway.RoleChanges);
    }

    [Fact]
    public async Task ASupporterWithNoDiscordAccountIsEndedWhenTheMonthRunsOut()
    {
        await using var setup = await SetUpAsync(paidDaysAgo: 90);
        await using (var db = setup.Services.Database.NewContext())
        {
            await db.Supporters.ExecuteUpdateAsync(u => u.SetProperty(s => s.DiscordUserId, (string?)null), Ct);
        }

        await PassAsync(setup.Services, new FakeGateway());

        Assert.Equal(SupporterStates.Ended, (await ReadAsync(setup)).State);
    }

    [Fact]
    public async Task ARefusedRoleIsShownOnTheSupporter_AndNotTriedAgainAtOnce()
    {
        await using var setup = await SetUpAsync();
        var gateway = new FakeGateway { RoleError = "Missing Permissions" };

        await PassAsync(setup.Services, gateway);

        var supporter = await ReadAsync(setup);
        Assert.NotNull(supporter.Problem);
        Assert.NotNull(supporter.TryAgainAt);
        Assert.Null(supporter.GivenDiscordRoleId);

        gateway.RoleError = null;
        var soon = await PassAsync(setup.Services, gateway);
        Assert.Equal(0, soon.Given);

        setup.Services.Clock.Advance(SupporterRoles.TryAgainAfter + TimeSpan.FromMinutes(1));
        var later = await PassAsync(setup.Services, gateway);
        Assert.Equal(2, later.Given);
        Assert.Null((await ReadAsync(setup)).Problem);
    }

    [Fact]
    public async Task WithNoThankYouMessageNoneIsSent_AndNobodyIsThankedLaterForOldPayments()
    {
        await using var setup = await SetUpAsync(thanks: null);
        var gateway = new FakeGateway();

        await PassAsync(setup.Services, gateway);

        Assert.Empty(gateway.DirectMessages);
        Assert.NotNull((await ReadAsync(setup)).ThanksSentAt);
    }
}
