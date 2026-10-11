using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The show and hide button's place: how a settings file names it, and where each place puts the
/// button. It is a fixture, so both places are locked.
/// </summary>
public class OverlayButtonTests
{
    [Theory]
    [InlineData("corner", ButtonPlace.Corner)]
    [InlineData("wrist", ButtonPlace.Wrist)]
    [InlineData("Wrist", ButtonPlace.Wrist)]
    [InlineData(" WRIST ", ButtonPlace.Wrist)]
    public void ASettingsFileNamesThePlace(string text, ButtonPlace expected)
        => Assert.Equal(expected, OverlayButton.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("somewhere else")]
    [InlineData("7")]
    public void AnythingMissingOrUnknownIsTheCorner(string? text)
        => Assert.Equal(ButtonPlace.Corner, OverlayButton.Parse(text));

    [Theory]
    [InlineData(ButtonPlace.Corner)]
    [InlineData(ButtonPlace.Wrist)]
    public void AWrittenPlaceReadsBackAsItself(ButtonPlace place)
        => Assert.Equal(place, OverlayButton.Parse(OverlayButton.Written(place)));

    [Fact]
    public void ThePlacesHavePlainNames()
    {
        Assert.Equal("Corner", OverlayButton.Name(ButtonPlace.Corner));
        Assert.Equal("Wrist", OverlayButton.Name(ButtonPlace.Wrist));
    }

    [Theory]
    [InlineData(ButtonPlace.Corner)]
    [InlineData(ButtonPlace.Wrist)]
    public void TheButtonIsAlwaysLockedAndSolid(ButtonPlace place)
    {
        var placement = OverlayButton.ToPlacement(place);

        Assert.True(placement.Locked);
        Assert.False(placement.ClickThrough);
        Assert.Equal(1f, placement.Opacity);
    }

    [Fact]
    public void TheCornerIsTheLowerLeftOfTheViewOnTheHead()
    {
        var placement = OverlayButton.ToPlacement(ButtonPlace.Corner);

        Assert.Equal(OverlayAnchor.Head, placement.Anchor);
        Assert.True(placement.Offset.X < 0f, "Left of centre, away from the panel's default place on the right.");
        Assert.True(placement.Offset.Y < 0f, "Below the eye line.");
        Assert.True(placement.Offset.Z < 0f, "In front of the eyes.");
        Assert.Equal(OverlayButton.CornerWidth, placement.Width);
    }

    [Fact]
    public void TheWristIsTheLeftOne()
    {
        var placement = OverlayButton.ToPlacement(ButtonPlace.Wrist);

        Assert.Equal(OverlayAnchor.LeftHand, placement.Anchor);
        Assert.Equal(OverlayPlacement.WristOffset, placement.Offset);
        Assert.Equal(OverlayButton.WristWidth, placement.Width);
    }

    [Fact]
    public void TheShortcutTextIsTheDefaultOne()
        => Assert.Equal("Stick back 5s", OverlayButton.DefaultShortcut);
}
