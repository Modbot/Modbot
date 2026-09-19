using Modbot.Companion.Clips;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// What the Save a clip control on the overlay panel says, and when it can be pressed.
/// </summary>
/// <remarks>
/// A moderator wearing a headset has no settings screen, no file explorer and no notification. A
/// control that looked pressable and quietly did nothing would leave them believing they had kept
/// a moment they had not, and finding out otherwise an hour later is the whole failure. So every
/// state where nothing is being recorded is a caption naming that and a control the tap path
/// refuses.
/// </remarks>
public class ClipButtonRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 21, 0, 0, TimeSpan.Zero);

    private static readonly ClipsFolderCheck Folder = new(@"C:\Videos\Modbot Clips", null);

    private static ClipsStatus Status(ClipRecordingState state, bool on = true) => new(
        ClipSettings.Default with { On = on },
        state,
        Folder,
        0,
        0);

    [Fact]
    public void WithClipsOffThereIsNoControlAtAll()
    {
        // Somebody who never asked for this does not find it in front of them in VR.
        var button = ClipButtonRule.For(Status(ClipRecordingState.Off, on: false), Now);

        Assert.Equal(ClipButtonState.Hidden, button.State);
        Assert.False(button.IsVisible);
        Assert.False(button.CanPress);
    }

    [Fact]
    public void WhileTheLastFewMinutesAreBeingKeptItSaysSoAndCanBePressed()
    {
        var button = ClipButtonRule.For(Status(ClipRecordingState.Recording), Now);

        Assert.Equal(ClipButtonState.Ready, button.State);
        Assert.Equal("Save a clip", button.Caption);
        Assert.True(button.CanPress);
    }

    [Theory]
    [InlineData(ClipRecordingState.Waiting, "Waiting for VRChat")]
    [InlineData(ClipRecordingState.NoWindow, "Waiting for VRChat's window")]
    [InlineData(ClipRecordingState.NothingRecordedYet, "Nothing recorded yet")]
    [InlineData(ClipRecordingState.FolderUnusable, "The folder cannot be used")]
    [InlineData(ClipRecordingState.NotOnThisMachine, "Not available on this machine")]
    [InlineData(ClipRecordingState.Failed, "Recording stopped")]
    public void WhenNothingIsBeingKeptItSaysWhyAndCannotBePressed(ClipRecordingState state, string caption)
    {
        var button = ClipButtonRule.For(Status(state), Now);

        Assert.Equal(ClipButtonState.Stopped, button.State);
        Assert.Equal(caption, button.Caption);
        Assert.False(button.CanPress);
        Assert.True(button.IsVisible);
    }

    [Fact]
    public void AfterAClipLandsItConfirms()
    {
        // The only way to find out from inside a headset.
        var button = ClipButtonRule.For(
            Status(ClipRecordingState.Recording),
            Now,
            new ClipSave(Now - TimeSpan.FromSeconds(2), Worked: true));

        Assert.Equal(ClipButtonState.Saved, button.State);
        Assert.Equal("Clip saved", button.Caption);
        Assert.True(button.CanPress);
    }

    [Fact]
    public void AfterASaveThatProducedNoFileItSaysThatInstead()
    {
        var button = ClipButtonRule.For(
            Status(ClipRecordingState.Recording),
            Now,
            new ClipSave(Now - TimeSpan.FromSeconds(2), Worked: false));

        Assert.Equal(ClipButtonState.NotSaved, button.State);
        Assert.Equal("Clip not saved", button.Caption);
    }

    [Fact]
    public void TheConfirmationGoesAwayAgain()
    {
        // A panel that kept saying "Clip saved" all evening would say nothing about the next one.
        var button = ClipButtonRule.For(
            Status(ClipRecordingState.Recording),
            Now,
            new ClipSave(Now - ClipButtonRule.SaidFor - TimeSpan.FromSeconds(1), Worked: true));

        Assert.Equal(ClipButtonState.Ready, button.State);
    }

    [Fact]
    public void ASaveFromTheFutureIsNotShownAsAConfirmation()
    {
        // A clock that moved backwards between the save and the draw. Nothing is claimed.
        var button = ClipButtonRule.For(
            Status(ClipRecordingState.Recording),
            Now,
            new ClipSave(Now + TimeSpan.FromMinutes(5), Worked: true));

        Assert.Equal(ClipButtonState.Ready, button.State);
    }

    [Fact]
    public void AConfirmationDoesNotSurviveVRChatClosing()
    {
        // "Clip saved" beside a recorder that has stopped reads as though it is still going.
        var button = ClipButtonRule.For(
            Status(ClipRecordingState.Waiting),
            Now,
            new ClipSave(Now, Worked: true));

        Assert.Equal(ClipButtonState.Stopped, button.State);
        Assert.False(button.CanPress);
    }
}
