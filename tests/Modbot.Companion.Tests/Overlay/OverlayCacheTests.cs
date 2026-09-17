using Modbot.Companion.Overlay;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The overlay's highest-value moment is "a flagged user just joined", which is also the moment
/// the network is most likely to be hurting. These tests pin the rule that makes that survivable:
/// stale data with its age on screen, never a spinner and never a blank.
/// </summary>
public class OverlayCacheTests
{
    private static InstanceContext Context(string instanceId = "39911") => new(
        instanceId,
        [
            new RosterMember("usr_8f2c", "Rin", RosterStanding.Flagged, 2, ["prior kick"]),
            new RosterMember("usr_aa", "Mei", RosterStanding.Member, 0, []),
        ]);

    [Fact]
    public void RemembersWhatOneServerSaidAboutOneInstance()
    {
        var cache = new OverlayCache(new FakeClock());

        cache.RecordContext("cats", Context());

        var cached = cache.Context("39911");
        Assert.Equal(Freshness.Fresh, cached.Freshness);
        Assert.Equal(2, cached.Value!.Members.Count);
        Assert.Equal("cats", cache.ServerId);
    }

    [Fact]
    public void SaysNotLoadedRatherThanPretendingAnEmptyRoster()
    {
        // The only honest blank. An empty roster and "I have never been told" are different
        // statements and a moderator must not be shown one when the truth is the other.
        var cached = new OverlayCache(new FakeClock()).Context("39911");

        Assert.Equal(Freshness.Never, cached.Freshness);
        Assert.Null(cached.Value);
        Assert.Equal("not loaded", cached.Describe());
    }

    [Fact]
    public void KeepsRenderingTheLastKnownRosterWhenEveryServerIsUnreachable()
    {
        var clock = new FakeClock();
        var cache = new OverlayCache(clock);
        cache.RecordContext("cats", Context());

        clock.Advance(TimeSpan.FromMinutes(20));
        cache.RecordUnreachable();

        var cached = cache.Context("39911");

        Assert.True(cache.ServerUnreachable);
        Assert.NotNull(cached.Value);
        Assert.Equal(Freshness.Stale, cached.Freshness);
        Assert.Equal("as of 20 minutes ago", cached.Describe());
    }

    [Theory]
    [InlineData(90, "as of 90 seconds ago")]
    [InlineData(20 * 60, "as of 20 minutes ago")]
    [InlineData(3 * 60 * 60, "as of 3 hours ago")]
    public void StatesTheAgeInWordsAModeratorCanActOn(int ageSeconds, string expected)
    {
        var clock = new FakeClock();
        var cache = new OverlayCache(clock);
        cache.RecordContext("cats", Context());

        clock.Advance(TimeSpan.FromSeconds(ageSeconds));

        Assert.Equal(expected, cache.Context("39911").Describe());
    }

    [Fact]
    public void AFreshFetchClearsTheUnreachableMarker()
    {
        var cache = new OverlayCache(new FakeClock());
        cache.RecordUnreachable();

        cache.RecordContext("cats", Context());

        Assert.False(cache.ServerUnreachable);
    }

    [Fact]
    public void HoldsProfileSummariesTheSameWay()
    {
        var clock = new FakeClock();
        var cache = new OverlayCache(clock);

        cache.RecordUser("cats", new UserSummary(
            "usr_8f2c", "Rin", RosterStanding.Flagged, 2,
            new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), ["prior kick"], ["member"]));

        clock.Advance(TimeSpan.FromMinutes(5));
        var cached = cache.User("usr_8f2c");

        Assert.Equal(Freshness.Stale, cached.Freshness);
        Assert.Equal(2, cached.Value!.PriorActions);
    }

    [Fact]
    public void ClearingLeavesNothingOfThatGroupsData()
    {
        var cache = new OverlayCache(new FakeClock());
        cache.RecordContext("cats", Context());

        cache.Clear();

        Assert.Equal(Freshness.Never, cache.Context("39911").Freshness);
    }

    [Fact]
    public void TwoInstancesAreRememberedSeparately()
    {
        var cache = new OverlayCache(new FakeClock());

        cache.RecordContext("cats", Context("39911"));
        cache.RecordContext("cats", Context("85019"));

        Assert.Equal(Freshness.Fresh, cache.Context("39911").Freshness);
        Assert.Equal(Freshness.Fresh, cache.Context("85019").Freshness);
    }
}
