using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Spec 4.3.5: what a moderator presses draws from the <c>interactive</c> backstop, sized to the
/// room spec 4.2 leaves under the ceiling, and never from the <c>global</c> bucket the sweeps
/// keep empty. A person's read of one user has a budget of its own, apart from both sync reads.
/// </summary>
public class InteractiveBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members = new(VRChatEndpointClass.GroupsMembers, "grp_test");
    private static readonly VRChatEndpoint Bans = new(VRChatEndpointClass.GroupsBans, "grp_test");
    private static readonly VRChatEndpoint AuditLog = new(VRChatEndpointClass.GroupsAuditLog, "grp_test");
    private static readonly VRChatEndpoint Moderate = new(VRChatEndpointClass.GroupsModerate, "grp_test");
    private static readonly VRChatEndpoint Write = new(VRChatEndpointClass.ModerationWrite, "grp_test");
    private static readonly VRChatEndpoint Lookup = new(VRChatEndpointClass.UsersLookup);
    private static readonly VRChatEndpoint Profile = new(VRChatEndpointClass.UsersProfile);
    private static readonly VRChatEndpoint User = new(VRChatEndpointClass.UsersRead);

    [Fact]
    public void TheInteractiveBackstopIsTheRoomLeftUnderTheCeiling()
    {
        var global = VRChatRateLimits.Defaults[VRChatEndpointClass.Global];
        var interactive = VRChatRateLimits.Defaults[VRChatEndpointClass.Interactive];

        // Spec 4.2: two a second, of which the scheduled classes take 1.425 and the rest is for
        // interactive work. The two backstops together are the ceiling, and neither shares a
        // token with the other.
        var scheduled = VRChatRateLimits.Scheduled.Sum(c => VRChatRateLimits.Defaults[c].HardMaxPerSecond);

        Assert.Equal(VRChatRateLimits.ScheduledTotalPerSecond, scheduled, 9);
        Assert.Equal(global.HardMaxPerSecond - scheduled, interactive.HardMaxPerSecond, 9);
        Assert.Equal(0.575, interactive.HardMaxPerSecond, 9);

        // A backstop passes through no backstop of its own, and is never a queue anybody enters.
        Assert.Null(global.Backstop);
        Assert.Null(interactive.Backstop);
        Assert.NotEqual(VRChatRateLimits.GroupLane, interactive.Lane);
        Assert.Equal(1, interactive.BurstTokens);
    }

    [Fact]
    public void WhatAModeratorPressesPassesThroughInteractive_AndBackgroundSyncThroughGlobal()
    {
        Assert.Equal(VRChatEndpointClass.Interactive, VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsModerate].Backstop);
        Assert.Equal(VRChatEndpointClass.Interactive, VRChatRateLimits.Defaults[VRChatEndpointClass.ModerationWrite].Backstop);

        foreach (var scheduled in VRChatRateLimits.Scheduled)
            Assert.Equal(VRChatEndpointClass.Global, VRChatRateLimits.Defaults[scheduled].Backstop);

        // Nothing under the interactive backstop is scheduled: it is what the room is for, not
        // part of what consumes it.
        var underInteractive = VRChatRateLimits.Defaults.Values
            .Where(c => c.Backstop == VRChatEndpointClass.Interactive)
            .Select(c => c.Name);

        Assert.DoesNotContain(underInteractive, VRChatRateLimits.Scheduled.Contains);
    }

    /// <summary>
    /// The contention this exists to remove: the sweeps take the global token, and a ban that
    /// used to wait for the next one now does not wait at all.
    /// </summary>
    [Fact]
    public async Task ABanDoesNotWaitForAGlobalTokenTheSweepsJustTook()
    {
        var harness = new LimiterHarness();

        // Three sweeps in a row drain the global bucket: burst 1, and each waits for the next
        // token at two a second.
        await harness.CallAsync(Members, ct: Ct);
        await harness.CallAsync(Bans, ct: Ct);
        await harness.CallAsync(AuditLog, ct: Ct);

        Assert.True(harness.Delays.Delays > 0, "the sweeps were expected to have waited on the global bucket");

        var before = harness.Clock.UtcNow;
        var delaysBefore = harness.Delays.Delays;

        var ban = await harness.CallAsync(Moderate, ct: Ct);

        Assert.True(ban.IsAcquired);
        Assert.Equal(before, harness.Clock.UtcNow);
        Assert.Equal(delaysBefore, harness.Delays.Delays);
    }

    [Fact]
    public async Task WhatAModeratorPressesIsPacedTogetherAtTheRoomLeft()
    {
        var harness = new LimiterHarness();

        // The first action spends the interactive token; the next, on a different class under
        // the same backstop, waits for the backstop's next token at 0.575 a second.
        await harness.CallAsync(Moderate, ct: Ct);
        var start = harness.Clock.UtcNow;

        await harness.CallAsync(Write, ct: Ct);

        var waited = harness.Clock.UtcNow - start;
        Assert.True(
            waited >= TimeSpan.FromSeconds(1.0 / 0.575) - TimeSpan.FromMilliseconds(50),
            $"the second action waited {waited}, less than the interactive backstop allows");
        Assert.True(waited < TimeSpan.FromSeconds(2), $"the second action waited {waited}, as if behind a two-second class");
    }

    [Fact]
    public async Task A429OnABanHalvesInteractiveAndGlobal_AndStopsOnlyTheBan()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, ct: Ct);
        await harness.CallAsync(Moderate, status: 429, ct: Ct);

        // The most specific bucket is cold; its ancestors and the global bucket are halved as
        // evidence, the way a 429 on users.read halves global (spec 4.2.5).
        Assert.True((await harness.HealthAsync($"{VRChatEndpointClass.GroupsModerate}:grp_test")).IsColdStopped);
        Assert.False((await harness.HealthAsync(VRChatEndpointClass.Interactive)).IsColdStopped);
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.Interactive)).BudgetMultiplier, 9);
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.Global)).BudgetMultiplier, 9);

        // The sweeps are neither stopped nor slowed by their own class.
        Assert.Equal(1.0, (await harness.HealthAsync($"{VRChatEndpointClass.GroupsMembers}:grp_test")).BudgetMultiplier, 9);
        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
    }

    [Fact]
    public void APersonsLookupHasItsOwnBudget_ApartFromBothSyncReads()
    {
        var lookup = VRChatRateLimits.Defaults[VRChatEndpointClass.UsersLookup];
        var profile = VRChatRateLimits.Defaults[VRChatEndpointClass.UsersProfile];
        var user = VRChatRateLimits.Defaults[VRChatEndpointClass.UsersRead];

        // Spec 4.2.5's original 1 req/s, deliberately under the sync reads' 3.5 on the same endpoints.
        Assert.Equal(1.0, lookup.HardMaxPerSecond, 9);
        Assert.True(lookup.HardMaxPerSecond < profile.HardMaxPerSecond);

        Assert.NotEqual(profile.Lane, lookup.Lane);
        Assert.NotEqual(user.Lane, lookup.Lane);

        // Exempt from the backstops for the reason its siblings are: the endpoint, not the caller.
        Assert.Null(lookup.Backstop);
        Assert.True(lookup.ServiceAccount);
        Assert.DoesNotContain(VRChatEndpointClass.UsersLookup, VRChatRateLimits.Scheduled);
    }

    [Fact]
    public async Task AColdProfileSyncDoesNotStopAPersonsLookup_AndTheOtherWayRound()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Profile, status: 429, ct: Ct);
        Assert.False((await harness.CallAsync(Profile, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Lookup, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(User, ct: Ct)).IsAcquired);

        await harness.CallAsync(Lookup, status: 429, ct: Ct);
        Assert.False((await harness.CallAsync(Lookup, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(User, ct: Ct)).IsAcquired);
    }
}
