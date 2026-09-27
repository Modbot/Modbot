using Modbot.Api.Features.Analytics.Server;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>The day a Discord server was made, read from its id for the "Est." line.</summary>
public class ServerProfileTests
{
    [Fact]
    public void AnId_CarriesTheMomentItWasMade()
    {
        // The example Discord's own documentation gives for reading an id.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_462_015_105_796),
            ServerProfileQuery.CreatedAt("175928847299117063"));

        Assert.Equal(
            new DateTimeOffset(2016, 4, 30, 11, 18, 25, 796, TimeSpan.Zero),
            ServerProfileQuery.CreatedAt("175928847299117063"));
    }

    [Fact]
    public void Zero_IsTheStartOfDiscordsCount()
        => Assert.Equal(new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero), ServerProfileQuery.CreatedAt("0"));

    [Fact]
    public void Spaces_AroundTheId_AreIgnored()
        => Assert.Equal(ServerProfileQuery.CreatedAt("175928847299117063"), ServerProfileQuery.CreatedAt(" 175928847299117063 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("99999999999999999999999")]
    public void AnIdThatIsNotANumber_HasNoDate(string? id)
        => Assert.Null(ServerProfileQuery.CreatedAt(id));
}
