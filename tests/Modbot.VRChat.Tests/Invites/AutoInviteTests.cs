using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Users;
using Modbot.TestSupport;
using Modbot.VRChat.Invites;

namespace Modbot.VRChat.Tests.Invites;

/// <summary>
/// Auto-invites design: who gets invited, who never does, how fast, and what is written down.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AutoInviteTests(PostgresFixture fixture) : AutoInviteTestBase(fixture)
{
    /// <summary>The usual setting: a companion in an open instance with one stranger in it.</summary>
    private async Task ReadyAsync(int minutesHere = 10)
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await AddPersonAsync(Stranger, accountDays: 400, trustRank: TrustRank.TrustedUser, verified18Plus: true);
        await WatchedArrivalAsync(Stranger, Clock.UtcNow.AddMinutes(-minutesHere));
    }

    // ── The switch ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingHappensWhileTheSwitchIsOff()
    {
        await ReadyAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
        Assert.Null(await InviteRowAsync(Stranger));
    }

    [Fact]
    public async Task SomebodyWhoPassesEverythingIsInvited()
    {
        await ReadyAsync();
        await SwitchOnAsync();

        Assert.True(await RunAsync());
        Assert.Equal([Stranger], VRChat.Groups.InvitedUserIds);
    }

    // ── The five minutes ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyWhoHasNotBeenThereLongEnoughIsNotInvited()
    {
        await ReadyAsync(minutesHere: 4);
        await SwitchOnAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task FiveMinutesIsTheFloorEvenWhenTheRowSaysLess()
    {
        // A row edited in the database by hand, or written by an older build. The pass clamps on
        // read, so the floor holds wherever the number came from (design §4.1).
        await ReadyAsync(minutesHere: 2);
        await SwitchOnAsync(minutes: 1);

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task ALongerWaitThanFiveMinutesIsHonoured()
    {
        await ReadyAsync(minutesHere: 10);
        await SwitchOnAsync(minutes: 30);

        Assert.False(await RunAsync());

        Clock.Advance(TimeSpan.FromMinutes(25));

        Assert.True(await RunAsync());
        Assert.Equal([Stranger], VRChat.Groups.InvitedUserIds);
    }

    // ── Who is never invited ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyAlreadyInTheGroupIsNeverInvited()
    {
        await ReadyAsync();
        await AddMemberAsync(Stranger);
        await SwitchOnAsync();

        Assert.False(await RunAsync());

        // Checked before sending, not after: no request was made at all.
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task SomebodyBannedIsNeverInvited()
    {
        await ReadyAsync();
        await AddBanAsync(Stranger);
        await SwitchOnAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task SomebodyTheGroupHasKickedBeforeIsNeverInvited()
    {
        await ReadyAsync();
        await ThrownOutAsync(Stranger, FactType.MemberKicked);
        await SwitchOnAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task ModbotDoesNotInviteTheAccountItSignsInAs()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await SignedInAsAsync(Stranger);
        await AddPersonAsync(Stranger, accountDays: 400);
        await WatchedArrivalAsync(Stranger, Clock.UtcNow.AddMinutes(-10));
        await SwitchOnAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    // ── Not twice ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSamePersonIsNotInvitedTwice()
    {
        await ReadyAsync();
        await SwitchOnAsync();

        Assert.True(await RunAsync());

        // Well past the thirty seconds, so only the remembered invite can be stopping this.
        Clock.Advance(TimeSpan.FromHours(2));

        Assert.False(await RunAsync());
        Assert.Single(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task TheSamePersonMayBeInvitedAgainOnceTheWaitHasPassed()
    {
        await ReadyAsync();
        await SwitchOnAsync(againAfterDays: 1);

        Assert.True(await RunAsync());

        Clock.Advance(TimeSpan.FromDays(2));

        Assert.True(await RunAsync());
        Assert.Equal([Stranger, Stranger], VRChat.Groups.InvitedUserIds);

        var row = await InviteRowAsync(Stranger);
        Assert.NotNull(row);
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public async Task AnInviteVRChatRefusedStillCountsAsAnAttempt()
    {
        await ReadyAsync();
        await SwitchOnAsync();
        VRChat.Groups.InviteStatus = System.Net.HttpStatusCode.Forbidden;

        Assert.False(await RunAsync());

        Clock.Advance(TimeSpan.FromHours(2));
        VRChat.Groups.InviteStatus = System.Net.HttpStatusCode.OK;

        // Not retried: a person VRChat keeps saying no about must not be asked every pass.
        Assert.False(await RunAsync());
        Assert.Single(VRChat.Groups.InvitedUserIds);

        var row = await InviteRowAsync(Stranger);
        Assert.NotNull(row);
        Assert.False(row.Worked);
        Assert.NotNull(row.Problem);
    }

    // ── The pacing ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoMoreThanOneInviteEveryThirtySeconds()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await SwitchOnAsync();

        foreach (var id in new[] { "usr_a", "usr_b", "usr_c" })
        {
            await AddPersonAsync(id, accountDays: 400);
            await WatchedArrivalAsync(id, Clock.UtcNow.AddMinutes(-10));
        }

        Assert.True(await RunAsync());
        Assert.Single(VRChat.Groups.InvitedUserIds);

        // Immediately again, and twenty-nine seconds later: still one.
        Assert.False(await RunAsync());
        Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await RunAsync());
        Assert.Single(VRChat.Groups.InvitedUserIds);

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await RunAsync());
        Assert.Equal(2, VRChat.Groups.InvitedUserIds.Count);
    }

    [Fact]
    public async Task TheThirtySecondsSurvivesARestart()
    {
        await ReadyAsync();
        await AddPersonAsync("usr_b", accountDays: 400);
        await WatchedArrivalAsync("usr_b", Clock.UtcNow.AddMinutes(-10));
        await SwitchOnAsync();

        Assert.True(await RunAsync());

        // Every pass runs in a fresh scope with a fresh context, which is what a restart leaves
        // behind. The gap is remembered in the row, not in the process.
        Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(await RunAsync());
        Assert.Single(VRChat.Groups.InvitedUserIds);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NobodyIsInvitedWhileNoCompanionIsReporting()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await AddPersonAsync(Stranger, accountDays: 400);
        await UnwatchedArrivalAsync(Stranger, Clock.UtcNow.AddHours(-3));
        await SwitchOnAsync();

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task TimeIsCountedFromWhenWatchingStartedNotFromAGuess()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await AddPersonAsync(Stranger, accountDays: 400);

        // They have been there for hours; the moderator walked in one minute ago. Modbot knows
        // only that they are here, so the five minutes runs from the moment watching began.
        await WatchedArrivalAsync(
            Stranger, Clock.UtcNow.AddMinutes(-1), watchFrom: Clock.UtcNow.AddMinutes(-1));

        await SwitchOnAsync();

        Assert.False(await RunAsync());

        Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await RunAsync());
    }

    // ── The rules ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyWhoFailsARuleIsNotInvited()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();
        await AddPersonAsync(Stranger, accountDays: 3, trustRank: TrustRank.Visitor);
        await WatchedArrivalAsync(Stranger, Clock.UtcNow.AddMinutes(-10));

        await SwitchOnAsync(new GiveawayRule
        {
            Kind = GiveawayRuleKinds.AllOf,
            Rules =
            [
                new GiveawayRule { Kind = GiveawayRuleKinds.VRChatAccountDays, Amount = 30 },
            ],
        });

        Assert.False(await RunAsync());
        Assert.Empty(VRChat.Groups.InvitedUserIds);
    }

    [Fact]
    public async Task ThePassMovesOnToTheNextPersonWhenOneFailsTheRules()
    {
        await AddCompanionAsync();
        await OpenInstanceAsync();

        await AddPersonAsync("usr_new", accountDays: 1);
        await WatchedArrivalAsync("usr_new", Clock.UtcNow.AddMinutes(-40));

        await AddPersonAsync("usr_old", accountDays: 900);
        await WatchedArrivalAsync("usr_old", Clock.UtcNow.AddMinutes(-10));

        await SwitchOnAsync(new GiveawayRule
        {
            Kind = GiveawayRuleKinds.AllOf,
            Rules =
            [
                new GiveawayRule { Kind = GiveawayRuleKinds.VRChatAccountDays, Amount = 30 },
            ],
        });

        Assert.True(await RunAsync());
        Assert.Equal(["usr_old"], VRChat.Groups.InvitedUserIds);
    }

    // ── The record ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInviteWritesAFact()
    {
        await ReadyAsync(minutesHere: 12);
        await SwitchOnAsync();

        Assert.True(await RunAsync());

        var facts = await FactsOfTypeAsync(FactType.GroupAutoInvited);
        var fact = Assert.Single(facts);

        Assert.Equal(Stranger, fact.SubjectId);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal(FactSource.Modbot, fact.Source);
        Assert.Equal(Instance, fact.InstanceId);

        // No actor: Modbot decided this on its own.
        Assert.Null(fact.ActorId);
    }

    [Fact]
    public async Task AnInviteVRChatRefusedWritesTheFailureFactAndNotTheOtherOne()
    {
        await ReadyAsync();
        await SwitchOnAsync();
        VRChat.Groups.InviteStatus = System.Net.HttpStatusCode.Forbidden;

        Assert.False(await RunAsync());

        Assert.Empty(await FactsOfTypeAsync(FactType.GroupAutoInvited));
        Assert.Single(await FactsOfTypeAsync(FactType.GroupAutoInviteFailed));
    }

    // ── The pieces the tests above need ──────────────────────────────────────────────────

    private async Task ThrownOutAsync(string userId, string type)
    {
        await using var context = Database.NewContext();

        await new Modbot.Analytics.Facts.EventPartitionMaintainer(context, Clock).EnsureAsync(Ct);

        await new Modbot.Analytics.Facts.FactWriter(context, Clock).WriteAsync(
            new Modbot.Analytics.Facts.FactRecord
            {
                Type = type,
                OccurredAt = Now.AddDays(-30),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = userId,
                Source = FactSource.AuditLog,
            },
            Ct);
    }

    private async Task SignedInAsAsync(string userId)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatSessionUserId = userId;
        await context.SaveChangesAsync(Ct);
    }
}
