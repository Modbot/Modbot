using Modbot.Companion.Clips;
using Modbot.Companion.Pipeline;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// When the client keeps the last few minutes, and — far more importantly — when it does not.
/// </summary>
/// <remarks>
/// These are the rules the rewritten promise in <c>Program.cs</c> makes to a moderator: off unless
/// switched on, and nothing recorded unless VRChat is running. They are a test rather than a
/// paragraph for the same reason the screen-capture ban was.
/// </remarks>
public class ClipRecordingRuleTests
{
    private static readonly ClipsFolderCheck Good = new(@"C:\Videos\Modbot Clips", null);

    private static readonly ClipsFolderCheck Bad = new(@"C:\Videos\Modbot Clips", "Nothing can be written to that folder.");

    [Theory]
    [InlineData(LogHealthStatus.Idle)]
    [InlineData(LogHealthStatus.Healthy)]
    [InlineData(LogHealthStatus.NotUnderstood)]
    public void NothingIsRecordedWhileTheSwitchIsOff(LogHealthStatus log)
    {
        // Whatever VRChat is doing. Off is a person saying they do not want this, and the honest
        // answer is to do none of the work.
        var state = ClipRecordingRule.Decide(ClipSettings.Default, log, Good, supported: true);

        Assert.Equal(ClipRecordingState.Off, state);
        Assert.False(ClipRecordingRule.ShouldRecord(state));
    }

    [Fact]
    public void WithTheSwitchOnAndVRChatRunningItRecords()
    {
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Good,
            supported: true);

        Assert.Equal(ClipRecordingState.Recording, state);
        Assert.True(ClipRecordingRule.ShouldRecord(state));
    }

    [Fact]
    public void WithTheSwitchOnAndVRChatNotRunningItWaits()
    {
        // The point of the rule. A moderator who switched this on for VRChat did not switch it on
        // for the rest of what they do on their PC.
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Idle,
            Good,
            supported: true);

        Assert.Equal(ClipRecordingState.Waiting, state);
        Assert.False(ClipRecordingRule.ShouldRecord(state));
    }

    [Fact]
    public void ALogTheClientNoLongerUnderstandsStillCountsAsVRChatRunning()
    {
        // Lines are arriving, so the moderator is in a world. A client that needs updating should
        // not also quietly stop the recording somebody switched on.
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.NotUnderstood,
            Good,
            supported: true);

        Assert.Equal(ClipRecordingState.Recording, state);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenToStopsItAndSaysSo()
    {
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Bad,
            supported: true);

        Assert.Equal(ClipRecordingState.FolderUnusable, state);
        Assert.False(ClipRecordingRule.ShouldRecord(state));
    }

    [Fact]
    public void AMachineThatCannotRecordSaysThatRatherThanFailingQuietly()
    {
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Good,
            supported: false);

        Assert.Equal(ClipRecordingState.NotOnThisMachine, state);
    }

    [Fact]
    public void AMachineThatCannotRecordIsStillOffWhenTheSwitchIsOff()
    {
        // "Off" beats "not available": a moderator who never turned this on is not told their
        // machine is deficient at something they did not ask for.
        var state = ClipRecordingRule.Decide(ClipSettings.Default, LogHealthStatus.Healthy, Good, supported: false);

        Assert.Equal(ClipRecordingState.Off, state);
    }

    [Fact]
    public void ARecorderThatAlreadyFailedIsNotTriedAgainEverySecond()
    {
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Good,
            supported: true,
            failed: true);

        Assert.Equal(ClipRecordingState.Failed, state);
        Assert.False(ClipRecordingRule.ShouldRecord(state));
    }

    [Fact]
    public void SavingIsOnlyOfferedWhileSomethingIsActuallyBeingKept()
    {
        Assert.False(ClipsStatus.None.CanSave);

        Assert.False((ClipsStatus.None with { State = ClipRecordingState.Waiting }).CanSave);
        Assert.False((ClipsStatus.None with { State = ClipRecordingState.NoWindow }).CanSave);
        Assert.True((ClipsStatus.None with { State = ClipRecordingState.Recording }).CanSave);
    }

    [Fact]
    public void VRChatRunningWithoutAWindowFoundYetIsItsOwnAnswer()
    {
        // A clip is VRChat's window, so there is nothing to keep until Windows has handed one
        // over. It happens for a second or two every time VRChat starts.
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Good,
            supported: true,
            windowFound: false);

        Assert.Equal(ClipRecordingState.NoWindow, state);
    }

    [Fact]
    public void ARecorderWithoutAWindowIsLeftRunningRatherThanRebuiltEverySecond()
    {
        // The recorder is the thing that asks Windows for VRChat's window. Taking it down for not
        // having found one would tear it down and build it again once a second for as long as
        // VRChat took to draw its window.
        Assert.True(ClipRecordingRule.ShouldRecord(ClipRecordingState.NoWindow));
        Assert.True(ClipRecordingRule.ShouldRecord(ClipRecordingState.Recording));

        foreach (var stopped in new[]
        {
            ClipRecordingState.Off,
            ClipRecordingState.Waiting,
            ClipRecordingState.NotOnThisMachine,
            ClipRecordingState.FolderUnusable,
            ClipRecordingState.Failed,
        })
        {
            Assert.False(ClipRecordingRule.ShouldRecord(stopped));
        }
    }

    [Fact]
    public void AWindowThatHasBeenFoundIsSimplyRecording()
    {
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Healthy,
            Good,
            supported: true,
            windowFound: true);

        Assert.Equal(ClipRecordingState.Recording, state);
    }

    [Fact]
    public void VRChatNotRunningBeatsNotHavingItsWindow()
    {
        // "Waiting for VRChat" is the true and useful answer; "waiting for VRChat's window" when
        // VRChat is not running at all would send somebody looking for the wrong problem.
        var state = ClipRecordingRule.Decide(
            ClipSettings.Default with { On = true },
            LogHealthStatus.Idle,
            Good,
            supported: true,
            windowFound: false);

        Assert.Equal(ClipRecordingState.Waiting, state);
    }
}
