using System.Text.Json.Nodes;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Where the chat window on a monitor was left and how big it was made: the size stays between one
/// and two times the default, the object reads back what was written, and a file with none reads
/// as no spot.
/// </summary>
public class DesktopChatSpotTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-settings-tests", Guid.NewGuid().ToString("n"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private static string? NoEnvironment(string name) => null;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private void Write(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
    }

    [Fact]
    public void ASpotReadsBackWhatWasWritten()
    {
        var spot = new DesktopChatSpot(120, -40, 600, 800);

        Assert.Equal(spot, DesktopChatSpot.FromJson(spot.ToJson()));
    }

    [Theory]
    [InlineData(100, DesktopChatSpot.DefaultWidth)]
    [InlineData(DesktopChatSpot.DefaultWidth, DesktopChatSpot.DefaultWidth)]
    [InlineData(600, 600)]
    [InlineData(5000, DesktopChatSpot.DefaultWidth * 2)]
    [InlineData(double.NaN, DesktopChatSpot.DefaultWidth)]
    public void TheWidthStaysBetweenOneAndTwoTimesTheDefault(double asked, double expected)
        => Assert.Equal(expected, DesktopChatSpot.ClampWidth(asked));

    [Theory]
    [InlineData(100, DesktopChatSpot.DefaultHeight)]
    [InlineData(700, 700)]
    [InlineData(5000, DesktopChatSpot.DefaultHeight * 2)]
    [InlineData(double.PositiveInfinity, DesktopChatSpot.DefaultHeight)]
    public void TheHeightStaysBetweenOneAndTwoTimesTheDefault(double asked, double expected)
        => Assert.Equal(expected, DesktopChatSpot.ClampHeight(asked));

    [Fact]
    public void ASizeOutsideTheBoundsIsBroughtInsideWhenRead()
    {
        var node = JsonNode.Parse("""{ "left": 10, "top": 20, "width": 10, "height": 99999 }""");

        var spot = DesktopChatSpot.FromJson(node);

        Assert.Equal(new DesktopChatSpot(10, 20, DesktopChatSpot.DefaultWidth, DesktopChatSpot.DefaultHeight * 2), spot);
    }

    [Fact]
    public void AMissingSizeTakesTheDefaultSize()
    {
        var spot = DesktopChatSpot.FromJson(JsonNode.Parse("""{ "left": 10, "top": 20 }"""));

        Assert.Equal(new DesktopChatSpot(10, 20, DesktopChatSpot.DefaultWidth, DesktopChatSpot.DefaultHeight), spot);
    }

    [Theory]
    [InlineData("""{ "width": 500, "height": 600 }""")]
    [InlineData("""{ "left": "far", "top": 3 }""")]
    [InlineData("""[1, 2]""")]
    public void ASpotWithNoReadablePositionReadsAsNone(string json)
        => Assert.Null(DesktopChatSpot.FromJson(JsonNode.Parse(json)));

    [Fact]
    public void AFileWithNoSpotReadsAsNone()
    {
        Write("""{ "overlayOn": false }""");

        Assert.Null(CompanionSettings.Load(SettingsPath, NoEnvironment).DesktopChatSpot);
    }

    [Fact]
    public void ASavedSpotIsReadBackWithTheRestOfTheFileKept()
    {
        Write("""{ "overlayOn": false, "overlayPushSpeed": 7 }""");
        var spot = new DesktopChatSpot(300, 150, 700, 900);

        Assert.True(CompanionSettings.SaveDesktopChatSpot(SettingsPath, spot));

        var loaded = CompanionSettings.Load(SettingsPath, NoEnvironment);
        Assert.Equal(spot, loaded.DesktopChatSpot);
        Assert.False(loaded.OverlayOn);
        Assert.Equal(7, loaded.OverlayPushSpeed);
    }
}
