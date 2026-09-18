using Modbot.Core.Giveaways;

namespace Modbot.Core.Tests.Giveaways;

/// <summary>
/// Giveaways design §5.2 and §5.4: the seed keeps its promise, the pick is reproducible from the
/// frozen list and the seed alone, and nothing in it is a floating-point number.
/// </summary>
public class GiveawayDrawTests
{
    private static IReadOnlyList<GiveawayTicket> Hat(params (string Key, long Weight)[] people)
        => [.. people.Select((p, i) => new GiveawayTicket(i, p.Key, p.Weight))];

    [Fact]
    public void TheRevealedSeedMatchesThePromiseMadeBeforehand()
    {
        var seed = GiveawayDraws.NewSeed();
        var promise = GiveawayDraws.Promise(seed);

        Assert.True(GiveawayDraws.Keeps(promise, seed));
        Assert.False(GiveawayDraws.Keeps(promise, GiveawayDraws.NewSeed()));
    }

    [Fact]
    public void ThePromiseIsSixtyFourHexCharactersAndSaysNothingAboutTheSeed()
    {
        var seed = GiveawayDraws.NewSeed();
        var promise = GiveawayDraws.Promise(seed);

        Assert.Equal(64, promise.Length);
        Assert.Equal(64, seed.Length);
        Assert.DoesNotContain(seed, promise, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePromiseComesOutOfTheSameSeedEveryTime()
    {
        Assert.Equal(
            GiveawayDraws.Promise("0123456789abcdef"),
            GiveawayDraws.Promise("0123456789abcdef"));
    }

    /// <summary>
    /// The whole of the fairness claim: somebody with the snapshot and the seed gets the same
    /// answer, and gets it again however many times they ask.
    /// </summary>
    [Fact]
    public void ADrawIsReproducibleFromTheSnapshotAndTheSeed()
    {
        var hat = Hat(("a", 1), ("b", 5), ("c", 2), ("d", 9), ("e", 3));
        const string Seed = "1f2e3d4c5b6a79880123456789abcdef1f2e3d4c5b6a79880123456789abcdef";

        var first = GiveawayDraws.Draw(hat, 3, Seed);
        var again = GiveawayDraws.Draw(hat, 3, Seed);

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(w => w.Key), again.Select(w => w.Key));
        Assert.Equal([1, 2, 3], first.Select(w => w.Rank));
    }

    /// <summary>
    /// The list is walked in position order, so a snapshot handed to somebody else in a different
    /// order would give a different answer -- which is why the position is stored.
    /// </summary>
    [Fact]
    public void ThePickFollowsThePositionsAndNotTheOrderTheyWerePassedIn()
    {
        var hat = Hat(("a", 1), ("b", 1), ("c", 1));
        const string Seed = "aa";

        var inOrder = GiveawayDraws.Draw(hat, 1, Seed);
        var shuffled = GiveawayDraws.Draw([.. hat.Reverse()], 1, Seed);

        Assert.Equal(inOrder[0].Key, shuffled[0].Key);
    }

    [Fact]
    public void ADifferentSeedGivesADifferentDraw()
    {
        // Enough people that two seeds agreeing by chance would be a surprise rather than a coin toss.
        var hat = Hat([.. Enumerable.Range(0, 200).Select(i => ($"p{i}", 1L))]);

        var first = GiveawayDraws.Draw(hat, 1, "seed-one");
        var second = GiveawayDraws.Draw(hat, 1, "seed-two");

        Assert.NotEqual(first[0].Key, second[0].Key);
    }

    [Fact]
    public void NobodyIsDrawnTwice()
    {
        var hat = Hat([.. Enumerable.Range(0, 20).Select(i => ($"p{i}", (long)(i + 1)))]);

        var winners = GiveawayDraws.Draw(hat, 5, "a-seed");

        Assert.Equal(5, winners.Count);
        Assert.Equal(5, winners.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AskingForMoreWinnersThanThereArePeopleDrawsEverybodyOnce()
    {
        var winners = GiveawayDraws.Draw(Hat(("a", 1), ("b", 1)), 5, "a-seed");

        Assert.Equal(2, winners.Count);
    }

    [Fact]
    public void NobodyWithNoWeightIsEverDrawn()
    {
        var hat = Hat(("kept-out", 0), ("in", 1), ("also-out", 0));

        var winners = GiveawayDraws.Draw(hat, 3, "a-seed");

        Assert.Single(winners);
        Assert.Equal("in", winners[0].Key);
    }

    [Fact]
    public void AnEmptyHatDrawsNobodyRatherThanThrowing()
    {
        Assert.Empty(GiveawayDraws.Draw([], 3, "a-seed"));
        Assert.Empty(GiveawayDraws.Draw(Hat(("a", 0)), 3, "a-seed"));
    }

    /// <summary>
    /// Weight is what makes the draw unfair on purpose, so the heavier ticket has to win more
    /// often. Counted over the seeds rather than asserted on one, since one draw proves nothing.
    /// </summary>
    [Fact]
    public void AHeavierTicketWinsMoreOften()
    {
        var hat = Hat(("light", 1), ("heavy", 99));
        var heavy = 0;

        for (var i = 0; i < 200; i++)
        {
            if (GiveawayDraws.Draw(hat, 1, $"seed-{i}")[0].Key == "heavy")
                heavy++;
        }

        Assert.True(heavy > 170, $"the heavier ticket won {heavy} times out of 200");
    }

    [Fact]
    public void TheRoundNumberIsTheSameEveryTimeAndDiffersPerRound()
    {
        Assert.Equal(GiveawayDraws.RoundNumber("seed", 0), GiveawayDraws.RoundNumber("seed", 0));
        Assert.NotEqual(GiveawayDraws.RoundNumber("seed", 0), GiveawayDraws.RoundNumber("seed", 1));
        Assert.NotEqual(GiveawayDraws.RoundNumber("seed", 0), GiveawayDraws.RoundNumber("other", 0));
    }

    // ── Weights ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWeightIsNeverBelowOne()
    {
        Assert.Equal(1, GiveawayWeight.Of(0m, null));
        Assert.Equal(1, GiveawayWeight.Of(-5m, null));
        Assert.Equal(1, GiveawayWeight.Of(0.2m, null));
    }

    [Fact]
    public void AWeightIsARoundedWholeNumber()
    {
        Assert.Equal(12, GiveawayWeight.Of(12.4m, null));
        Assert.Equal(13, GiveawayWeight.Of(12.5m, null));
    }

    /// <summary>
    /// §5.5: the cap is what stops one very dedicated person holding most of the probability.
    /// </summary>
    [Fact]
    public void TheCapHoldsTheHeaviestTicketDown()
    {
        Assert.Equal(20, GiveawayWeight.Of(400m, 20));
        Assert.Equal(15, GiveawayWeight.Of(15m, 20));

        // And it does not lift anybody: the floor is still one.
        Assert.Equal(1, GiveawayWeight.Of(0m, 20));
    }

    [Fact]
    public void ACapMakesTheHeaviestTicketOnlyAsLikelyAsTheCapAllows()
    {
        // Without a cap this person would hold 400 of 404 tickets.
        var capped = Hat(
            ("devoted", GiveawayWeight.Of(400m, 20)),
            ("a", GiveawayWeight.Of(2m, 20)),
            ("b", GiveawayWeight.Of(1m, 20)),
            ("c", GiveawayWeight.Of(1m, 20)));

        var devoted = 0;

        for (var i = 0; i < 200; i++)
        {
            if (GiveawayDraws.Draw(capped, 1, $"seed-{i}")[0].Key == "devoted")
                devoted++;
        }

        // 20 of 24 tickets is about five in six; well short of the 99 in 100 an uncapped draw gives.
        Assert.InRange(devoted, 130, 190);
    }
}
