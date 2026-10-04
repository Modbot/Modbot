using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.HeadsUps;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// Who is told that the heads-ups in an instance changed: the devices standing there, by the same
/// rule as a flagged-join alert, and nobody else.
/// </summary>
public class HeadsUpSignalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyTheDevicesInThatInstanceAreFound()
    {
        var locations = new DeviceLocations();
        var here = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        var otherWorld = Guid.NewGuid();
        var stale = Guid.NewGuid();

        locations.Record(here, "12345", Now, "wrld_a");
        locations.Record(elsewhere, "99999", Now, "wrld_a");
        locations.Record(otherWorld, "12345", Now, "wrld_b");
        locations.Record(stale, "12345", Now - DeviceLocations.RememberedFor - TimeSpan.FromMinutes(1), "wrld_a");

        Assert.Equal([here], locations.DevicesIn("12345", Now, "wrld_a"));
    }

    [Fact]
    public async Task TellingADeviceMovesItsCountAndWakesItsWait()
    {
        var signal = new HeadsUpSignal();
        var device = Guid.NewGuid();

        var before = signal.VersionOf(device);
        var next = signal.NextAsync(device);
        Assert.False(next.IsCompleted);

        signal.Tell([device]);

        await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, signal.VersionOf(device));
        Assert.False(signal.NextAsync(device).IsCompleted);
    }

    [Fact]
    public void ADeviceNotToldIsNotWoken()
    {
        var signal = new HeadsUpSignal();
        var told = Guid.NewGuid();
        var other = Guid.NewGuid();
        var otherNext = signal.NextAsync(other);

        signal.Tell([told]);

        Assert.False(otherNext.IsCompleted);
        Assert.Equal(0, signal.VersionOf(other));
    }
}
