using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// Which device a line plays through: the chosen one when it is there, the default when it is
/// not, and "the default, whatever it is now" when nothing was chosen.
/// </summary>
public class OutputDeviceChoiceTests
{
    private sealed class Devices(params OutputDevice[] devices) : IOutputDevices
    {
        public IReadOnlyList<OutputDevice> List() => devices;

        public OutputDevice? Default() => devices.FirstOrDefault();
    }

    private static readonly OutputDevice Speakers = new("{0.0.0.00000000}.{spk}", "Speakers (Realtek)");
    private static readonly OutputDevice Headset = new("{0.0.0.00000000}.{hmd}", "Headset (Index)");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoChoiceFollowsTheSystemDefault(string? wanted)
    {
        // Null on the way out, rather than today's default device, is the point: the player
        // opens "the default" at play time, so a default the operating system moved between two
        // lines is followed without anybody re-reading settings.
        var choice = OutputDeviceChoice.Resolve(wanted, new Devices(Speakers, Headset));

        Assert.Null(choice.Device);
        Assert.False(choice.FellBack);
    }

    [Fact]
    public void AChosenDeviceIsUsedWhenPresent()
    {
        var choice = OutputDeviceChoice.Resolve(Headset.Id, new Devices(Speakers, Headset));

        Assert.Equal(Headset, choice.Device);
        Assert.False(choice.FellBack);
    }

    [Fact]
    public void AChosenDeviceThatIsGoneFallsBackToTheDefault()
    {
        var choice = OutputDeviceChoice.Resolve(Headset.Id, new Devices(Speakers));

        Assert.Null(choice.Device);
        Assert.True(choice.FellBack);
    }

    [Fact]
    public void IdsAreMatchedExactly()
    {
        var choice = OutputDeviceChoice.Resolve(Headset.Id.ToUpperInvariant(), new Devices(Speakers, Headset));

        Assert.True(choice.FellBack);
    }

    [Fact]
    public void AMachineWithNoOutputAtAllStillResolves()
    {
        var choice = OutputDeviceChoice.Resolve(Headset.Id, new Devices());

        Assert.Null(choice.Device);
        Assert.True(choice.FellBack);
    }
}
