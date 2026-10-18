using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The show and hide button's place: how a settings file names it, and where each place puts the
/// button. It starts unlocked and solid, like the other panels (it was a locked fixture until 2026-10-17).
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
    public void TheButtonStartsUnlockedAndSolidAsTheOtherPanelsDo(ButtonPlace place)
    {
        var placement = OverlayButton.ToPlacement(place);

        Assert.False(placement.Locked);
        Assert.False(placement.ClickThrough);
        Assert.Equal(1f, placement.Opacity);
        Assert.Equal(OverlayPlacement.Default.Locked, placement.Locked);
        Assert.Equal(NotifyOverlaySettings.Default.ToPlacement().Locked, placement.Locked);
    }

    [Fact]
    public void TheCornerIsAtTheButtonsOwnFractionsAndLeavesThePopUpsAlone()
    {
        var button = OverlayButton.ToPlacement(ButtonPlace.Corner).Offset;

        Assert.Equal(-0.48f * OverlayButton.CornerDistance, button.X, 4);
        Assert.Equal(-0.34f * OverlayButton.CornerDistance, button.Y, 4);
        Assert.Equal(-OverlayButton.CornerDistance, button.Z, 4);

        // The pop-ups' own fractions are not moved by this.
        Assert.Equal(0.40f, NotifyOverlaySettings.AcrossFraction);
        Assert.Equal(0.26f, NotifyOverlaySettings.DownFraction);
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
        => Assert.Equal("Right stick back 5s", OverlayButton.DefaultShortcut);
}
