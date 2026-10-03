using Modbot.Core.Google;

namespace Modbot.Core.Tests.Google;

/// <summary>Google Calendar design §3.2: the ids Modbot gives its events on Google.</summary>
public class GoogleEventIdsTests
{
    private static readonly Guid Event = Guid.Parse("01923456-7890-7abc-8def-0123456789ab");

    [Fact]
    public void AnIdIsGooglesAlphabet_mbAndTheGuidAndTheTurn()
    {
        var id = GoogleEventIds.For(Event, 0);

        Assert.StartsWith("mb", id, StringComparison.Ordinal);
        Assert.Equal(2 + 26 + 1, id.Length);
        Assert.All(id, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'v', $"'{c}' is not base32hex"));
        Assert.InRange(id.Length, 5, 1024);
    }

    [Fact]
    public void TheSameEventAndTurnGiveTheSameId_AndAnotherEventAnother()
    {
        Assert.Equal(GoogleEventIds.For(Event, 3), GoogleEventIds.For(Event, 3));
        Assert.NotEqual(GoogleEventIds.For(Event, 0), GoogleEventIds.For(Guid.NewGuid(), 0));
    }

    [Fact]
    public void TheTurnIsReadBack_AndTheNextOneIsOneMore()
    {
        Assert.Equal(0, GoogleEventIds.TurnOf(GoogleEventIds.For(Event, 0)));
        Assert.Equal(12, GoogleEventIds.TurnOf(GoogleEventIds.For(Event, 12)));

        Assert.Equal(GoogleEventIds.For(Event, 1), GoogleEventIds.Next(Event, GoogleEventIds.For(Event, 0)));
        Assert.Equal(GoogleEventIds.For(Event, 0), GoogleEventIds.Next(Event, null));
    }

    [Fact]
    public void AnIdThatIsNotModbotsHasNoTurn()
    {
        Assert.Null(GoogleEventIds.TurnOf(null));
        Assert.Null(GoogleEventIds.TurnOf("abc123"));
        Assert.Null(GoogleEventIds.TurnOf(GoogleEventIds.For(Event, 0)[..28]));
    }
}
