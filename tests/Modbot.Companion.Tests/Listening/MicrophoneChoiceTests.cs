using Modbot.Companion.Listening;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// Which microphone the phrase listener opens: the one the moderator picked, or the Windows
/// default when they picked none — or when the one they picked is not plugged in.
/// </summary>
/// <remarks>
/// The same rule the voice's output follows, and deliberately the same rule: a headset left
/// switched off must not mean the phrase is never heard again with nothing on the screen saying
/// why.
/// </remarks>
public class MicrophoneChoiceTests
{
    private static readonly Microphone Desk = new("{desk}", "Desk microphone");

    private static readonly Microphone Headset = new("{headset}", "Index HMD Mic");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PickingNothingMeansTheWindowsDefault(string? wanted)
    {
        var choice = MicrophoneChoice.Resolve(wanted, [Desk, Headset]);

        Assert.Null(choice.Microphone);
        Assert.False(choice.FellBack);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void APickedMicrophoneThatIsPluggedInIsTheOneOpened()
    {
        var choice = MicrophoneChoice.Resolve(Headset.Id, [Desk, Headset]);

        Assert.Equal(Headset, choice.Microphone);
        Assert.False(choice.FellBack);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void APickedMicrophoneThatIsNotThereFallsBackToTheDefault()
    {
        // Not to silence. A headset left switched off would otherwise mean the phrase was never
        // heard again and nothing would say why.
        var choice = MicrophoneChoice.Resolve(Headset.Id, [Desk]);

        Assert.Null(choice.Microphone);
        Assert.True(choice.FellBack);
        Assert.NotNull(choice.Problem);
    }

    [Fact]
    public void AMachineWithNoMicrophonesAtAllStillFallsBackRatherThanFailing()
    {
        var choice = MicrophoneChoice.Resolve(Desk.Id, []);

        Assert.Null(choice.Microphone);
        Assert.True(choice.FellBack);
    }

    [Fact]
    public void TheIdIsMatchedExactly()
    {
        // Windows' own device ids are what they are; a different spelling is a different device,
        // and guessing which one somebody meant is worse than standing in with the default.
        var choice = MicrophoneChoice.Resolve(Headset.Id.ToUpperInvariant(), [Desk, Headset]);

        Assert.True(choice.FellBack);
    }

    [Fact]
    public void TheChoiceIsKeptSoTheMicrophoneIsUsedAgainWhenItIsBack()
    {
        // Falling back does not rewrite the settings: the same id, once the headset is on again,
        // opens the headset.
        var settings = new ListeningSettings(On: true, MicrophoneId: Headset.Id);

        Assert.True(MicrophoneChoice.Resolve(settings.MicrophoneId, [Desk]).FellBack);
        Assert.Equal(Headset, MicrophoneChoice.Resolve(settings.MicrophoneId, [Desk, Headset]).Microphone);
    }

    [Fact]
    public void ListeningIsStillOffAndOnNoMicrophoneUntilSomebodySaysOtherwise()
    {
        Assert.False(ListeningSettings.Default.On);
        Assert.Null(ListeningSettings.Default.MicrophoneId);
    }
}
