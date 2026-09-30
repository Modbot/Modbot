using Modbot.Api.Features.Places;
using Modbot.Core.Users;

namespace Modbot.Api.Tests.Features.Places;

public class PeoplePresentSeriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static PresenceSession Session(string user, int from, int to) => new(user, At(from), At(to));

    private static IReadOnlySet<string> Members(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, TrustRank?> Ranks(params (string User, TrustRank? Rank)[] ranks)
        => ranks.ToDictionary(r => r.User, r => r.Rank, StringComparer.Ordinal);

    [Fact]
    public void NoSessions_IsNoPoints()
    {
        Assert.Empty(PeoplePresentSeries.Build([], Members(), Ranks()));
    }

    /// <summary>Ada is a member and a User; Bob is neither a member nor read yet. Ada leaves at 30, Bob at 40.</summary>
    [Fact]
    public void EachArrivalAndLeave_IsAPoint_AndTheLastIsZeros()
    {
        var points = PeoplePresentSeries.Build(
            [Session("usr_ada", 0, 30), Session("usr_bob", 10, 40)],
            Members("usr_ada"),
            Ranks(("usr_ada", TrustRank.User)));

        Assert.Equal([At(0), At(10), At(30), At(40)], points.Select(p => p.At));
        Assert.Equal([1, 1, 0, 0], points.Select(p => p.Members));
        Assert.Equal([1, 1, 0, 0], points.Select(p => p.User));
        Assert.Equal([0, 1, 1, 0], points.Select(p => p.RankUnknown));
        Assert.Equal(0, points[^1].Members + points[^1].User + points[^1].RankUnknown);
    }

    [Fact]
    public void ChangesAtOneInstant_MakeOnePoint()
    {
        var points = PeoplePresentSeries.Build(
            [Session("usr_ada", 0, 10), Session("usr_bob", 10, 20)],
            Members(),
            Ranks(("usr_ada", TrustRank.Visitor), ("usr_bob", TrustRank.TrustedUser)));

        Assert.Equal([At(0), At(10), At(20)], points.Select(p => p.At));
        Assert.Equal([1, 0, 0], points.Select(p => p.Visitor));
        Assert.Equal([0, 1, 0], points.Select(p => p.TrustedUser));
    }

    [Fact]
    public void EveryRank_HasItsOwnCount()
    {
        var points = PeoplePresentSeries.Build(
            [
                Session("a", 0, 1), Session("b", 0, 1), Session("c", 0, 1), Session("d", 0, 1),
                Session("e", 0, 1), Session("f", 0, 1), Session("g", 0, 1), Session("h", 0, 1),
            ],
            Members(),
            Ranks(
                ("a", TrustRank.Visitor), ("b", TrustRank.NewUser), ("c", TrustRank.User), ("d", TrustRank.KnownUser),
                ("e", TrustRank.TrustedUser), ("f", TrustRank.Legend), ("g", TrustRank.Nuisance), ("h", TrustRank.VRChatTeam)));

        var first = points[0];
        Assert.Equal(
            [1, 1, 1, 1, 1, 1, 1, 1, 0],
            [first.Visitor, first.NewUser, first.User, first.KnownUser, first.TrustedUser, first.Legend, first.Nuisance, first.VRChatTeam, first.RankUnknown]);
    }

    [Fact]
    public void ANullRank_IsNotRead_NotAVisitor()
    {
        var points = PeoplePresentSeries.Build(
            [Session("usr_ada", 0, 10)],
            Members(),
            Ranks(("usr_ada", null)));

        Assert.Equal(0, points[0].Visitor);
        Assert.Equal(1, points[0].RankUnknown);
    }
}
