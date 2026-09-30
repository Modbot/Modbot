using Modbot.Companion.Overlay;
using Modbot.Core.Users;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// What each filter keeps and drops, on the Instance list and on the Audit Log, and the words a
/// join time is said in.
/// </summary>
public class ListFilteringTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Member(string name, RosterStanding standing = RosterStanding.Ordinary, TrustRank? rank = TrustRank.User)
        => new("usr_" + name, name, standing, 0, [], rank);

    private static readonly IReadOnlyList<RosterMember> People =
    [
        Member("Rin", RosterStanding.Flagged, TrustRank.NewUser),
        Member("Kai", RosterStanding.Staff, TrustRank.TrustedUser),
        Member("Mira", RosterStanding.Member, TrustRank.KnownUser),
        Member("Jo", RosterStanding.Ordinary, TrustRank.Visitor),
        Member("Ash", RosterStanding.Ordinary, null),
        Member("𝕬𝖑𝖊𝖝"),
    ];

    private static readonly Dictionary<string, DateTimeOffset?> Arrivals = new()
    {
        ["usr_Rin"] = Now.AddMinutes(-3),
        ["usr_Kai"] = null,
        ["usr_Mira"] = Now.AddMinutes(-12),
        ["usr_Jo"] = Now.AddMinutes(-90),
        ["usr_𝕬𝖑𝖊𝖝"] = Now.AddMinutes(-40),

        // Ash: the log never mentioned them.
    };

    private static List<string?> Names(ListFilters filters)
        => [.. ListFiltering.Roster(People, filters, Arrivals, Now).Select(m => m.DisplayName)];

    [Fact]
    public void NoFiltersIsEverybodyInTheUsualOrder()
    {
        Assert.Equal(["Rin", "Kai", "Mira", "𝕬𝖑𝖊𝖝", "Ash", "Jo"], Names(ListFilters.None));
    }

    [Theory]
    [InlineData(Who.Flagged, "Rin")]
    [InlineData(Who.Staff, "Kai")]
    [InlineData(Who.Members, "Mira")]
    public void WhoKeepsOneStanding(Who who, string only)
    {
        Assert.Equal([only], Names(new ListFilters(Who: who)));
    }

    [Fact]
    public void NotInGroupIsEverybodyWithNoStanding()
    {
        Assert.Equal(["𝕬𝖑𝖊𝖝", "Ash", "Jo"], Names(new ListFilters(Who: Who.NotInGroup)));
    }

    [Fact]
    public void RanksKeepAnyOfThePicked()
    {
        var picked = new RankPick().Toggle(TrustRank.NewUser).Toggle(TrustRank.Visitor);

        Assert.Equal(["Rin", "Jo"], Names(new ListFilters(Ranks: picked)));
    }

    [Fact]
    public void NotKnownIsSomebodyWhoseRankTheServerHasNotSaid()
    {
        Assert.Equal(["Ash"], Names(new ListFilters(Ranks: new RankPick().Toggle(null))));
    }

    [Fact]
    public void AJoinWindowKeepsWhoArrivedInsideIt()
    {
        Assert.Equal(["Rin"], Names(new ListFilters(Time: TimeWindow.FiveMinutes)));
        Assert.Equal(["Rin", "Mira"], Names(new ListFilters(Time: TimeWindow.FifteenMinutes)));
        Assert.Equal(["Rin", "Mira", "𝕬𝖑𝖊𝖝"], Names(new ListFilters(Time: TimeWindow.Hour)));
    }

    [Fact]
    public void EarlierIsOverAnHourAgoOrAlreadyHere()
    {
        Assert.Equal(["Kai", "Jo"], Names(new ListFilters(Time: TimeWindow.Earlier)));
    }

    [Fact]
    public void NoWindowClaimsSomebodyTheLogNeverMentioned()
    {
        foreach (var window in Enum.GetValues<TimeWindow>().Where(w => w is not TimeWindow.Any))
            Assert.DoesNotContain("Ash", Names(new ListFilters(Time: window)));
    }

    [Fact]
    public void ANameMatchesItsPlainLettersAndIgnoresCase()
    {
        Assert.Equal(["𝕬𝖑𝖊𝖝"], Names(new ListFilters(Name: "alex")));
        Assert.Equal(["Mira"], Names(new ListFilters(Name: "MIR")));
        Assert.Empty(Names(new ListFilters(Name: "zzz")));
    }

    [Fact]
    public void FiltersAllHaveToAgree()
    {
        var filters = new ListFilters(Who: Who.NotInGroup, Ranks: new RankPick().Toggle(TrustRank.Visitor).Toggle(TrustRank.User));

        Assert.Equal(["𝕬𝖑𝖊𝖝", "Jo"], Names(filters));
    }

    [Fact]
    public void NewestFirstPutsTheLatestArrivalOnTopAndTheUnknownLast()
    {
        Assert.Equal(["Rin", "Mira", "𝕬𝖑𝖊𝖝", "Jo", "Ash", "Kai"], Names(new ListFilters(Order: RosterOrder.Newest)));
    }

    [Fact]
    public void ByNameIsByTheNameInPlainLetters()
    {
        Assert.Equal(["𝕬𝖑𝖊𝖝", "Ash", "Jo", "Kai", "Mira", "Rin"], Names(new ListFilters(Order: RosterOrder.Name)));
    }

    [Fact]
    public void TheOrderHidesNobody()
    {
        Assert.False(new ListFilters(Order: RosterOrder.Newest).Hides);
        Assert.True(new ListFilters(Order: RosterOrder.Newest).AnyPicked);
        Assert.True(new ListFilters(Name: "x").Hides);
        Assert.False(ListFilters.None.AnyPicked);
    }

    [Theory]
    [InlineData(null, "already here")]
    [InlineData(0.5, "<1m")]
    [InlineData(5.2, "5m")]
    [InlineData(60.0, "1h")]
    [InlineData(75.0, "1h 15m")]
    public void JoinTimesAreSaidInFewWords(double? minutesAgo, string words)
    {
        DateTimeOffset? arrived = minutesAgo is { } m ? Now.AddMinutes(-m) : null;

        Assert.Equal(words, ListFiltering.JoinedWords(arrived, Now));
    }

    private static LiveEvent Event(string id, string kind, string? name, string? rank, int minutesAgo, RosterStanding standing = RosterStanding.Ordinary)
        => new(
            id,
            id,
            kind,
            Now.AddMinutes(-minutesAgo),
            "39911",
            name is null ? null : new LivePerson("usr_" + name, name, rank, standing, 0, []),
            kind == LiveEventKinds.FlaggedJoin,
            null,
            false);

    private static readonly IReadOnlyList<LiveEvent> Log =
    [
        Event("1", LiveEventKinds.PersonJoined, "Nox", "Visitor", 1),
        Event("2", LiveEventKinds.PersonLeft, "Ash", "User", 4),
        Event("3", LiveEventKinds.FlaggedJoin, "Rin", "NewUser", 10, RosterStanding.Flagged),
        Event("4", LiveEventKinds.PersonHere, "Kai", null, 70, RosterStanding.Staff),
        Event("5", LiveEventKinds.WatchStopped, null, null, 80),
    ];

    private static List<string> Ids(ListFilters filters)
        => [.. ListFiltering.Events(Log, filters, Now).Select(e => e.Id)];

    [Fact]
    public void KindsKeepAnyOfThePicked()
    {
        var picked = new KindPick().Toggle(LiveEventKinds.PersonJoined).Toggle(LiveEventKinds.PersonLeft);

        Assert.Equal(["1", "2"], Ids(new ListFilters(Kinds: picked)));
    }

    [Fact]
    public void AnAuditLogRowWithNobodyOnItHasNoRankNoStandingAndNoName()
    {
        Assert.DoesNotContain("5", Ids(new ListFilters(Who: Who.NotInGroup)));
        Assert.DoesNotContain("5", Ids(new ListFilters(Ranks: new RankPick().Toggle(null))));
        Assert.DoesNotContain("5", Ids(new ListFilters(Name: "a")));
    }

    [Fact]
    public void AnAuditLogRanksComeOffTheServersWord()
    {
        Assert.Equal(["3"], Ids(new ListFilters(Ranks: new RankPick().Toggle(TrustRank.NewUser))));
        Assert.Equal(["4"], Ids(new ListFilters(Ranks: new RankPick().Toggle(null))));
    }

    [Fact]
    public void AnAuditLogWindowIsHowLongAgoTheRowWas()
    {
        Assert.Equal(["1", "2"], Ids(new ListFilters(Time: TimeWindow.FiveMinutes)));
        Assert.Equal(["4", "5"], Ids(new ListFilters(Time: TimeWindow.Earlier)));
    }

    [Fact]
    public void AnAuditLogKeepsItsNewestFirstOrder()
    {
        Assert.Equal(["1", "2", "3", "4", "5"], Ids(ListFilters.None));
        Assert.Equal(["3", "4"], Ids(new ListFilters(Who: Who.Flagged)).Concat(Ids(new ListFilters(Who: Who.Staff))));
    }
}
