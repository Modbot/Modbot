using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// World lists design §4: every world once before any comes round again, a new round never starting
/// on the world just played, and the player range.
/// </summary>
public class WorldShuffleTests
{
    private static readonly string[] Worlds = ["wrld_a", "wrld_b", "wrld_c", "wrld_d", "wrld_e"];

    private static string PickOne(WorldListShuffle shuffle, IReadOnlyList<string> worlds, Random random, Func<string, bool>? fits = null)
    {
        WorldShuffle.Bring(shuffle, worlds, random);
        var world = WorldShuffle.Next(shuffle, fits ?? (_ => true));
        Assert.NotNull(world);
        WorldShuffle.Play(shuffle, world);
        return world;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    public void EveryWorldIsPlayedOnceBeforeAnyComesRoundAgain(int seed)
    {
        var random = new Random(seed);
        var shuffle = new WorldListShuffle();

        for (var round = 0; round < 4; round++)
        {
            var played = Enumerable.Range(0, Worlds.Length).Select(_ => PickOne(shuffle, Worlds, random)).ToList();
            Assert.Equal(Worlds.Order(StringComparer.Ordinal), played.Order(StringComparer.Ordinal));
        }

        Assert.Equal(4, shuffle.Round);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void ANewRoundNeverStartsOnTheWorldJustPlayed(int seed)
    {
        var random = new Random(seed);
        var shuffle = new WorldListShuffle();
        string? last = null;

        for (var pick = 0; pick < 60; pick++)
        {
            var world = PickOne(shuffle, Worlds, random);
            Assert.NotEqual(last, world);
            last = world;
        }
    }

    [Fact]
    public void AWorldAddedJoinsThisRound_AndOneTakenOutIsNeverPickedAgain()
    {
        var random = new Random(5);
        var shuffle = new WorldListShuffle();

        var first = PickOne(shuffle, Worlds, random);
        var gone = Worlds.First(w => w != first);
        string[] now = [.. Worlds.Where(w => w != gone), "wrld_new"];

        var rest = Enumerable.Range(0, now.Length - 1).Select(_ => PickOne(shuffle, now, random)).ToList();

        Assert.DoesNotContain(gone, rest);
        Assert.Contains("wrld_new", rest);
        Assert.Equal(now.Where(w => w != first).Order(StringComparer.Ordinal), rest.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void APutBackWorldIsStillDueThisRound()
    {
        var random = new Random(3);
        var shuffle = new WorldListShuffle();

        var first = PickOne(shuffle, Worlds, random);
        WorldShuffle.PutBack(shuffle, first);

        var all = Enumerable.Range(0, Worlds.Length).Select(_ => PickOne(shuffle, Worlds, random)).ToList();
        Assert.Equal(Worlds.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NextAfterAWorld_StartsAfterIt_GoesRound_AndNeverOffersIt()
    {
        var shuffle = new WorldListShuffle { Order = ["a", "b", "c", "d"], Played = ["b"], Round = 1 };

        Assert.Equal("d", WorldShuffle.Next(shuffle, _ => true, after: "c"));
        Assert.Equal("a", WorldShuffle.Next(shuffle, w => w != "d", after: "c"));
        Assert.Null(WorldShuffle.Next(new WorldListShuffle { Order = ["a"], Round = 1 }, _ => true, after: "a"));
    }

    [Fact]
    public void TheFirstUnplayedWorldThatFitsIsPicked_AndNoneIsNull()
    {
        var shuffle = new WorldListShuffle { Order = ["small", "big", "any"], Round = 1 };
        var ranges = new Dictionary<string, (int? Min, int? Max)>
        {
            ["small"] = (2, 4),
            ["big"] = (8, null),
            ["any"] = (null, null),
        };

        bool Fits(string w, int? people) => WorldShuffle.Fits(ranges[w].Min, ranges[w].Max, people);

        Assert.Equal("big", WorldShuffle.Next(shuffle, w => Fits(w, 10)));
        Assert.Equal("small", WorldShuffle.Next(shuffle, w => Fits(w, 3)));
        Assert.Equal("any", WorldShuffle.Next(shuffle, w => Fits(w, 6)));

        shuffle.Played = ["any"];
        Assert.Null(WorldShuffle.Next(shuffle, w => Fits(w, 6)));
    }

    [Theory]
    [InlineData(null, null, 50, true)]
    [InlineData(2, 8, 1, false)]
    [InlineData(2, 8, 2, true)]
    [InlineData(2, 8, 8, true)]
    [InlineData(2, 8, 9, false)]
    [InlineData(4, null, 400, true)]
    [InlineData(null, 6, 7, false)]
    [InlineData(2, 8, null, true)]
    public void APlayerRangeFitsTheCount_AndAnUnknownCountFitsEverything(int? min, int? max, int? people, bool fits) =>
        Assert.Equal(fits, WorldShuffle.Fits(min, max, people));

    [Theory]
    [InlineData("https://vrchat.com/home/world/wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b", "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b")]
    [InlineData("https://vrchat.com/home/world/wrld_abc/info", "wrld_abc")]
    [InlineData("https://vrchat.com/home/launch?worldId=wrld_abc&instanceId=123~group(grp_x)", "wrld_abc")]
    [InlineData("  wrld_abc  ", "wrld_abc")]
    // Legacy ids follow no pattern (foundation §3.1.1): taken as typed.
    [InlineData("o_d3b0e2a1-legacy", "o_d3b0e2a1-legacy")]
    [InlineData("https://vrchat.com/home/user/usr_x", null)]
    [InlineData("", null)]
    public void TheWorldIdIsFoundInALinkOrTakenAsTyped(string text, string? expected) =>
        Assert.Equal(expected, WorldLinks.WorldIdFrom(text));
}
