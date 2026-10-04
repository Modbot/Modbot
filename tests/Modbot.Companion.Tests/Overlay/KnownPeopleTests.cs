using Modbot.Companion.Overlay;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// What the server last said about each person, kept a short while after they leave, so the card
/// about somebody leaving can carry the same marks as the card about them arriving.
/// </summary>
public class KnownPeopleTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void WhatWasSaidIsKept()
    {
        var known = new KnownPeople(_clock);
        known.Note("usr_rin", new PersonInfo(TrustRank.TrustedUser, true));

        Assert.Equal(new PersonInfo(TrustRank.TrustedUser, true), known.InfoOf("usr_rin"));
        Assert.Null(known.InfoOf("usr_kai"));
    }

    [Fact]
    public void NothingSaidIsNothingKept()
    {
        var known = new KnownPeople(_clock);
        known.Note("usr_rin", null);

        Assert.Equal(0, known.Count);
    }

    [Fact]
    public void SomebodyWhoLeftIsKeptForAFewMinutes()
    {
        var known = new KnownPeople(_clock);
        known.Note("usr_rin", new PersonInfo(TrustRank.User, null));

        _clock.Advance(KnownPeople.Keep - TimeSpan.FromSeconds(1));
        Assert.NotNull(known.InfoOf("usr_rin"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(known.InfoOf("usr_rin"));
    }

    [Fact]
    public void NobodyIsHeldPastTheirTimeInALongSession()
    {
        // Let go of on the next note or look, not kept until the most is reached.
        var known = new KnownPeople(_clock);
        known.Note("usr_rin", new PersonInfo(TrustRank.User, null));
        known.Note("usr_kai", new PersonInfo(TrustRank.User, null));

        _clock.Advance(KnownPeople.Keep);
        known.Note("usr_ana", new PersonInfo(TrustRank.User, null));
        Assert.Equal(1, known.Count);

        _clock.Advance(KnownPeople.Keep);
        Assert.Null(known.InfoOf("usr_kai"));
        Assert.Equal(0, known.Count);
    }

    [Fact]
    public void SomebodyStillHereNeverRunsOut()
    {
        // Each roster read notes them again.
        var known = new KnownPeople(_clock);

        for (var i = 0; i < 10; i++)
        {
            known.Note("usr_rin", new PersonInfo(TrustRank.User, null));
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.NotNull(known.InfoOf("usr_rin"));
    }

    [Fact]
    public void ForgettingClearsEveryone()
    {
        var known = new KnownPeople(_clock);
        known.Note("usr_rin", new PersonInfo(TrustRank.User, null));

        known.Forget();

        Assert.Null(known.InfoOf("usr_rin"));
        Assert.Equal(0, known.Count);
    }

    [Fact]
    public void NeverMoreThanTheMostKept()
    {
        var known = new KnownPeople(_clock);

        for (var i = 0; i < KnownPeople.MostKept + 20; i++)
        {
            known.Note($"usr_{i}", new PersonInfo(TrustRank.User, null));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(KnownPeople.MostKept, known.Count);

        // The oldest went first.
        Assert.Null(known.InfoOf("usr_0"));
        Assert.NotNull(known.InfoOf($"usr_{KnownPeople.MostKept + 19}"));
    }
}
