using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Gateway;
using Modbot.Discord.Invites;
using Modbot.Discord.Tests.Fakes;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Invites;

/// <summary>
/// Which invite a new member used: the one whose use count went up. When none did, or more than one
/// did, the join is kept with no invite; a single-use invite that vanished as somebody joined is theirs.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InviteTrackerTests
{
    private const string Linktree = "linktree";
    private const string Partner = "partner";

    private readonly PostgresFixture _db;

    public InviteTrackerTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordInviteSnapshot Invite(string code, int uses, int maxUses = 0, bool vanity = false, DateTimeOffset? expiresAt = null)
        => new(code, uses, maxUses, "900", null, null, vanity, expiresAt);

    private static DiscordMemberJoin Join(string userId, bool bot = false)
        => new(SyncSetUp.Guild, userId, "someone", bot);

    /// <summary>Tracks the invites the gateway lists now, as the bot does when it connects.</summary>
    private static async Task SeedAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<InviteTracker>().ReadAsync([.. gateway.Invites], true, Ct);
    }

    private static async Task JoinAsync(TestServices services, FakeGateway gateway, DiscordMemberJoin join)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<InviteTracker>().JoinedAsync(gateway, join, Ct);
    }

    /// <summary>The code of the invite the person's join was counted under, or null for none.</summary>
    private static async Task<string?> CodeOfAsync(TestServices services, string userId)
    {
        await using var db = services.Database.NewContext();
        var join = await db.DiscordInviteJoins.AsNoTracking().SingleAsync(j => j.UserId == userId, Ct);

        return join.InviteId is { } id
            ? (await db.DiscordInviteLinks.AsNoTracking().SingleAsync(i => i.Id == id, Ct)).Code
            : null;
    }

    [Fact]
    public async Task The_invite_whose_count_went_up_is_the_one_used()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 4), Invite(Partner, 9)]);
        await SeedAsync(services, gateway);

        gateway.Invites[1] = Invite(Partner, 10);
        await JoinAsync(services, gateway, Join("5001"));

        Assert.Equal(Partner, await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task Two_joins_in_a_row_are_each_given_their_own_invite()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite(Partner, 0)]);
        await SeedAsync(services, gateway);

        gateway.Invites[0] = Invite(Linktree, 1);
        await JoinAsync(services, gateway, Join("5001"));

        gateway.Invites[1] = Invite(Partner, 1);
        await JoinAsync(services, gateway, Join("5002"));

        Assert.Equal(Linktree, await CodeOfAsync(services, "5001"));
        Assert.Equal(Partner, await CodeOfAsync(services, "5002"));
    }

    [Fact]
    public async Task The_vanity_link_is_a_source_of_its_own()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite("thy-kingdom", 20, vanity: true)]);
        await SeedAsync(services, gateway);

        gateway.Invites[1] = Invite("thy-kingdom", 21, vanity: true);
        await JoinAsync(services, gateway, Join("5001"));

        await using var db = services.Database.NewContext();
        var invite = await db.DiscordInviteLinks.AsNoTracking().SingleAsync(i => i.Code == "thy-kingdom", Ct);

        Assert.True(invite.IsVanity);
        Assert.Equal("thy-kingdom", await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task Two_invites_going_up_at_once_cannot_be_told_apart()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite(Partner, 0)]);
        await SeedAsync(services, gateway);

        gateway.Invites[0] = Invite(Linktree, 1);
        gateway.Invites[1] = Invite(Partner, 1);
        await JoinAsync(services, gateway, Join("5001"));

        Assert.Null(await CodeOfAsync(services, "5001"));

        // The counts are taken as they stand, so the next person is not blamed for these two.
        gateway.Invites[0] = Invite(Linktree, 2);
        await JoinAsync(services, gateway, Join("5002"));

        Assert.Equal(Linktree, await CodeOfAsync(services, "5002"));
    }

    [Fact]
    public async Task No_count_going_up_leaves_the_join_with_no_invite()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.Add(Invite(Linktree, 3));
        await SeedAsync(services, gateway);

        await JoinAsync(services, gateway, Join("5001"));

        Assert.Null(await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task A_single_use_invite_that_vanished_as_somebody_joined_is_theirs()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite("once", 0, maxUses: 1)]);
        await SeedAsync(services, gateway);

        // Discord deletes an invite as its last use is spent, and Modbot hears of it.
        gateway.Invites.RemoveAt(1);
        using (var scope = services.Scope())
            await scope.ServiceProvider.GetRequiredService<InviteTracker>().RemovedAsync("once", Ct);

        await JoinAsync(services, gateway, Join("5001"));

        Assert.Equal("once", await CodeOfAsync(services, "5001"));

        await using var db = services.Database.NewContext();
        Assert.NotNull((await db.DiscordInviteLinks.AsNoTracking().SingleAsync(i => i.Code == "once", Ct)).GoneAt);
    }

    [Fact]
    public async Task A_single_use_invite_that_vanished_before_Modbot_heard_it_was_deleted_is_still_theirs()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        var later = services.Clock.UtcNow.AddDays(1);
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite("once", 0, maxUses: 1, expiresAt: later)]);
        await SeedAsync(services, gateway);

        // The join comes first; the delete event follows it.
        gateway.Invites.RemoveAt(1);
        await JoinAsync(services, gateway, Join("5001"));
        using (var scope = services.Scope())
            await scope.ServiceProvider.GetRequiredService<InviteTracker>().RemovedAsync("once", Ct);

        Assert.Equal("once", await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task A_single_use_invite_that_has_expired_is_not_blamed_for_a_later_join()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        var past = services.Clock.UtcNow.AddHours(-2);
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite("once", 0, maxUses: 1, expiresAt: past)]);
        await SeedAsync(services, gateway);

        // Never heard of it going: it ran out of time and is not the one this person used.
        gateway.Invites.RemoveAt(1);
        await JoinAsync(services, gateway, Join("5001"));

        Assert.Null(await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task A_single_use_invite_marked_gone_long_ago_is_not_blamed_for_a_later_join()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.AddRange([Invite(Linktree, 0), Invite("once", 0, maxUses: 1)]);
        await SeedAsync(services, gateway);

        gateway.Invites.RemoveAt(1);
        using (var scope = services.Scope())
            await scope.ServiceProvider.GetRequiredService<InviteTracker>().RemovedAsync("once", Ct);

        services.Clock.Advance(InviteTracker.JustGone + TimeSpan.FromMinutes(5));
        await JoinAsync(services, gateway, Join("5001"));

        Assert.Null(await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task While_tracking_is_off_nothing_is_recorded()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.Add(Invite(Linktree, 0));
        await SeedAsync(services, gateway);

        gateway.Invites[0] = Invite(Linktree, 1);
        await JoinAsync(services, gateway, Join("5001"));

        await using var db = services.Database.NewContext();
        Assert.Empty(await db.DiscordInviteLinks.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await db.DiscordInviteJoins.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task A_bot_joining_is_not_counted()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.Add(Invite(Linktree, 0));
        await SeedAsync(services, gateway);

        gateway.Invites[0] = Invite(Linktree, 1);
        await JoinAsync(services, gateway, Join("5001", bot: true));

        await using var db = services.Database.NewContext();
        Assert.Empty(await db.DiscordInviteJoins.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task Without_permission_to_read_invites_the_join_is_kept_with_no_invite()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway { InvitesRefused = true };

        await JoinAsync(services, gateway, Join("5001"));

        Assert.Null(await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task An_invite_made_a_moment_ago_and_used_at_once_is_the_one_used()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.Add(Invite(Linktree, 0));
        await SeedAsync(services, gateway);

        // Made and used before Modbot heard of it.
        gateway.Invites.Add(new DiscordInviteSnapshot("fresh", 1, 0, "900", null, services.Clock.UtcNow.AddSeconds(-5)));
        await JoinAsync(services, gateway, Join("5001"));

        Assert.Equal("fresh", await CodeOfAsync(services, "5001"));
    }

    [Fact]
    public async Task A_deleted_invite_is_kept_and_marked_gone()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordInviteTrackingOn = true, Ct);
        var gateway = new FakeGateway();
        gateway.Invites.Add(Invite(Linktree, 0));
        await SeedAsync(services, gateway);

        using (var scope = services.Scope())
            await scope.ServiceProvider.GetRequiredService<InviteTracker>().RemovedAsync(Linktree, Ct);

        await using var db = services.Database.NewContext();
        Assert.NotNull((await db.DiscordInviteLinks.AsNoTracking().SingleAsync(i => i.Code == Linktree, Ct)).GoneAt);
    }
}
