using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The one comparison every refresh is decided by (user profile sync design §3.2), and the
/// queue built on it. No database: the order is a pure function and is pinned as one.
/// </summary>
public class RefreshOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    private static RefreshRequest At(string user, RefreshReason reason, int minutesAgo, int requestedMinutesAgo = 0) =>
        new(user, reason, T0.AddMinutes(-minutesAgo), T0.AddMinutes(-requestedMinutesAgo));

    /// <summary>
    /// An instance's location is not a person and is never queued, at any tier and whatever the
    /// caller says about freshness. Before this, one location row made every housekeeping pass
    /// spend a users.profile call on a 400.
    /// </summary>
    [Theory]
    [InlineData(RefreshReason.SeenInInstance)]
    [InlineData(RefreshReason.OpenedInModbot)]
    [InlineData(RefreshReason.SeenInFactLog)]
    [InlineData(RefreshReason.NeverRefreshed)]
    [InlineData(RefreshReason.ProfileIsOld)]
    public void AnInstancesLocationIsTurnedAwayAtEveryTier(RefreshReason reason)
    {
        var queue = new UserRefreshQueue();
        const string instance = "wrld_06c991da-951b-4ca5-b7d2-e3f5a9839e28:03044~group(grp_0a17232e)~groupAccessType(plus)~region(use)";

        var outcome = queue.Offer(At(instance, reason, minutesAgo: 0), null, T0, new UserProfileSyncOptions());

        Assert.Equal(RefreshRequestOutcome.NotAPerson, outcome);
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.PendingFor(instance));
    }

    /// <summary>The maintainer's order, top to bottom.</summary>
    [Fact]
    public void TiersComeBeforeAnythingElse()
    {
        var order = RefreshOrder.Instance;

        var inInstance = At("a", RefreshReason.SeenInInstance, minutesAgo: 60);
        var opened = At("b", RefreshReason.OpenedInModbot, minutesAgo: 0);
        var seen = At("c", RefreshReason.SeenInFactLog, minutesAgo: 0);
        var old = At("d", RefreshReason.ProfileIsOld, minutesAgo: 60 * 24);
        var never = At("e", RefreshReason.NeverRefreshed, minutesAgo: 0);

        // The instance sighting is the oldest key in the set and still wins: the tier decides.
        var sorted = new[] { never, old, seen, opened, inInstance }.Order(order).Select(r => r.UserId);

        Assert.Equal(["a", "b", "c", "d", "e"], sorted);
    }

    [Theory]
    [InlineData(RefreshReason.SeenInInstance)]
    [InlineData(RefreshReason.OpenedInModbot)]
    [InlineData(RefreshReason.SeenInFactLog)]
    [InlineData(RefreshReason.NeverRefreshed)]
    public void WithinATier_TheMostRecentGoesFirst(RefreshReason reason)
    {
        var older = At("older", reason, minutesAgo: 10);
        var newer = At("newer", reason, minutesAgo: 1);

        Assert.True(RefreshOrder.Instance.Compare(newer, older) < 0);
    }

    /// <summary>The one tier whose point is age: the profile nobody has looked at longest goes first.</summary>
    [Fact]
    public void AmongOldProfiles_TheOldestGoesFirst()
    {
        var older = At("older", RefreshReason.ProfileIsOld, minutesAgo: 60 * 12);
        var newer = At("newer", RefreshReason.ProfileIsOld, minutesAgo: 60 * 7);

        Assert.True(RefreshOrder.Instance.Compare(older, newer) < 0);
    }

    /// <summary>A total order, so a sorted set can hold two people with identical times.</summary>
    [Fact]
    public void TwoDifferentPeopleNeverCompareEqual()
    {
        var a = At("a", RefreshReason.SeenInFactLog, minutesAgo: 5);
        var b = At("b", RefreshReason.SeenInFactLog, minutesAgo: 5);

        Assert.NotEqual(0, RefreshOrder.Instance.Compare(a, b));
        Assert.Equal(0, RefreshOrder.Instance.Compare(a, a with { RequestedAt = T0.AddDays(1) }));
    }

    // ── IsAnsweredBy ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASightingIsAnsweredByARefreshAtOrAfterIt()
    {
        var sighting = At("a", RefreshReason.SeenInFactLog, minutesAgo: 5);

        Assert.True(sighting.IsAnsweredBy(T0.AddMinutes(-5)));
        Assert.True(sighting.IsAnsweredBy(T0.AddMinutes(-1)));
        Assert.False(sighting.IsAnsweredBy(T0.AddMinutes(-6)));
        Assert.False(sighting.IsAnsweredBy(null));
    }

    [Fact]
    public void AnOldProfileIsAnsweredOnlyByALaterRefresh()
    {
        var old = At("a", RefreshReason.ProfileIsOld, minutesAgo: 60 * 8);

        Assert.False(old.IsAnsweredBy(T0.AddHours(-8)));
        Assert.True(old.IsAnsweredBy(T0.AddHours(-7)));
    }

    [Fact]
    public void NeverRefreshedIsAnsweredByAnyRefreshAtAll()
    {
        var never = At("a", RefreshReason.NeverRefreshed, minutesAgo: 0);

        Assert.True(never.IsAnsweredBy(T0.AddYears(-1)));
        Assert.False(never.IsAnsweredBy(null));
    }

    // ── Fresh enough ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AModeratorsRequestIsFreshEnoughInsideTheGap()
    {
        var options = new UserProfileSyncOptions { FreshEnoughWhenOpened = TimeSpan.FromSeconds(30) };

        Assert.True(UserRefreshQueue.IsFreshEnough(T0.AddSeconds(-10), RefreshReason.OpenedInModbot, T0, options));
        Assert.False(UserRefreshQueue.IsFreshEnough(T0.AddSeconds(-31), RefreshReason.OpenedInModbot, T0, options));
        Assert.False(UserRefreshQueue.IsFreshEnough(null, RefreshReason.OpenedInModbot, T0, options));
    }

    /// <summary>Presence has its own, shorter gap: the strongest signal, but still not one fetch per fact.</summary>
    [Fact]
    public void APresenceSightingHasAShorterGapThanAModeratorsRequest()
    {
        var options = new UserProfileSyncOptions();

        Assert.True(options.FreshEnoughWhenSeenInInstance < options.FreshEnoughWhenOpened);
        Assert.True(UserRefreshQueue.IsFreshEnough(T0.AddSeconds(-5), RefreshReason.SeenInInstance, T0, options));
        Assert.False(UserRefreshQueue.IsFreshEnough(T0.AddSeconds(-20), RefreshReason.SeenInInstance, T0, options));
    }

    /// <summary>The durable tiers are never turned away on freshness: their reason is that the profile is not fresh.</summary>
    [Theory]
    [InlineData(RefreshReason.SeenInFactLog)]
    [InlineData(RefreshReason.ProfileIsOld)]
    [InlineData(RefreshReason.NeverRefreshed)]
    public void TheDurableTiersHaveNoFreshEnoughGap(RefreshReason reason)
        => Assert.False(UserRefreshQueue.IsFreshEnough(T0, reason, T0, new UserProfileSyncOptions()));
}

/// <summary>The queue: one entry per person, promoted upward, taken in order.</summary>
public class UserRefreshQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
    private static readonly UserProfileSyncOptions Options = new();

    private static RefreshRequest Request(string user, RefreshReason reason, int secondsAgo = 0) =>
        new(user, reason, T0.AddSeconds(-secondsAgo), T0);

    [Fact]
    public void TakesInTheOrderTheComparisonSays()
    {
        var queue = new UserRefreshQueue();

        queue.Offer(Request("never", RefreshReason.NeverRefreshed), null, T0, Options);
        queue.Offer(Request("seen", RefreshReason.SeenInFactLog), null, T0, Options);
        queue.Offer(Request("here", RefreshReason.SeenInInstance), null, T0, Options);
        queue.Offer(Request("opened", RefreshReason.OpenedInModbot), null, T0, Options);

        Assert.Equal("here", queue.TakeNext()!.UserId);
        Assert.Equal("opened", queue.TakeNext()!.UserId);
        Assert.Equal("seen", queue.TakeNext()!.UserId);
        Assert.Equal("never", queue.TakeNext()!.UserId);
        Assert.Null(queue.TakeNext());
    }

    /// <summary>A moderator clicking ten times is one fetch.</summary>
    [Fact]
    public void TheSamePersonIsQueuedOnce()
    {
        var queue = new UserRefreshQueue();

        Assert.Equal(RefreshRequestOutcome.Queued, queue.Offer(Request("a", RefreshReason.OpenedInModbot), null, T0, Options));
        Assert.Equal(RefreshRequestOutcome.AlreadyQueued, queue.Offer(Request("a", RefreshReason.OpenedInModbot), null, T0, Options));
        Assert.Equal(RefreshRequestOutcome.AlreadyQueued, queue.Offer(Request("a", RefreshReason.SeenInFactLog), null, T0, Options));

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void AHigherTierPromotesAPendingEntry()
    {
        var queue = new UserRefreshQueue();

        queue.Offer(Request("a", RefreshReason.NeverRefreshed), null, T0, Options);
        queue.Offer(Request("b", RefreshReason.SeenInFactLog), null, T0, Options);

        Assert.Equal(RefreshRequestOutcome.Promoted, queue.Offer(Request("a", RefreshReason.SeenInInstance), null, T0, Options));

        Assert.Equal(1, queue.CountByReason()[RefreshReason.SeenInInstance]);
        Assert.False(queue.CountByReason().ContainsKey(RefreshReason.NeverRefreshed));
        Assert.Equal("a", queue.TakeNext()!.UserId);
    }

    [Fact]
    public void AFreshEnoughProfileIsNotQueued()
    {
        var queue = new UserRefreshQueue();

        var outcome = queue.Offer(
            Request("a", RefreshReason.OpenedInModbot), lastRefreshedAt: T0.AddSeconds(-5), T0, Options);

        Assert.Equal(RefreshRequestOutcome.FreshEnough, outcome);
        Assert.Equal(0, queue.Count);
    }

    /// <summary>A screen asking "is this person being refreshed" is answered while the fetch is in flight, too.</summary>
    [Fact]
    public void AnEntryInFlightIsStillPendingUntilFinished()
    {
        var queue = new UserRefreshQueue();
        queue.Offer(Request("a", RefreshReason.OpenedInModbot), null, T0, Options);

        var taken = queue.TakeNext()!;

        Assert.NotNull(queue.PendingFor("a"));
        Assert.Same(taken, queue.InProgress);

        queue.Finish(taken);

        Assert.Null(queue.PendingFor("a"));
        Assert.Null(queue.InProgress);
    }

    /// <summary>A cold stop did not spend the request. It goes back where it was.</summary>
    [Fact]
    public void PutBackRestoresTheEntryAtItsTier()
    {
        var queue = new UserRefreshQueue();
        queue.Offer(Request("a", RefreshReason.SeenInInstance), null, T0, Options);
        queue.Offer(Request("b", RefreshReason.SeenInFactLog), null, T0, Options);

        var taken = queue.TakeNext()!;
        queue.PutBack(taken);

        Assert.Equal(2, queue.Count);
        Assert.Equal("a", queue.TakeNext()!.UserId);
    }
}
