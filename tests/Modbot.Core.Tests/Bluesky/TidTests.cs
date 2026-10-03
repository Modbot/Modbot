using Modbot.Core.Bluesky;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// Bluesky record keys (Bluesky design §3.4, atproto.com/specs/tid): thirteen characters of the
/// sortable base32 alphabet, made from Modbot's clock, in order.
/// </summary>
public class TidTests
{
    [Fact]
    public void ATidIsThirteenCharactersOfTheAlphabet()
    {
        var tid = Tid.At(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), clockId: 17);

        Assert.Equal(Tid.Length, tid.Length);
        Assert.All(tid, c => Assert.Contains(c, Tid.Alphabet));
        Assert.True(Tid.IsTid(tid));

        // The top bit is always 0, so the first character is one of the alphabet's first sixteen.
        Assert.InRange(Tid.Alphabet.IndexOf(tid[0], StringComparison.Ordinal), 0, 15);
    }

    [Fact]
    public void ATidCarriesTheTimeItWasMadeAt()
    {
        var at = new DateTimeOffset(2026, 10, 3, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);

        var tid = Tid.At(at, clockId: 0);

        Assert.Equal(at, Tid.TimeOf(tid));
    }

    [Fact]
    public void ALaterTimeSortsLater()
    {
        var first = Tid.At(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), clockId: 1023);
        var second = Tid.At(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(10), clockId: 0);

        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void NextComesFromTheClock_NotTheSystemClock()
    {
        // Later than every other test's clock here: Next never goes back past the last key it made in
        // this process, whichever test made it.
        var clock = new FakeClock(new DateTimeOffset(2099, 5, 6, 7, 8, 9, TimeSpan.Zero));

        var tid = Tid.Next(clock);

        var time = Tid.TimeOf(tid);
        Assert.NotNull(time);
        Assert.InRange(time!.Value, clock.UtcNow, clock.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void TwoMadeAtTheSameMomentStillComeOutInOrder_AndDiffer()
    {
        var clock = new FakeClock(new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var first = Tid.Next(clock);
        var second = Tid.Next(clock);

        Assert.NotEqual(first, second);
        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void AClockSetBackStillGivesALaterKey()
    {
        var clock = new FakeClock(new DateTimeOffset(2033, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var first = Tid.Next(clock);

        clock.UtcNow = clock.UtcNow.AddMinutes(-5);
        var second = Tid.Next(clock);

        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3jzfcijpj2z2")]
    [InlineData("3jzfcijpj2z2aa")]
    [InlineData("3jzfcijpj2z21")]
    [InlineData("zzzzzzzzzzzzz")]
    [InlineData("3JZFCIJPJ2Z2A")]
    public void WhatIsNotATidIsSaidSo(string? value)
    {
        Assert.False(Tid.IsTid(value));
        Assert.Null(Tid.TimeOf(value));
    }

    [Fact]
    public void AClockIdOutsideTenBitsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Tid.At(DateTimeOffset.UnixEpoch, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tid.At(DateTimeOffset.UnixEpoch, -1));
    }
}
