using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Live;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>Which paired companions are running right now, and where a computer last said it was.</summary>
public class CompanionConnectionsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADeviceWithNoConnectionIsNotOpen()
    {
        var connections = new CompanionConnections();

        Assert.False(connections.IsOpen([Guid.NewGuid()], Noon));
    }

    [Fact]
    public void ASocketCountsFromWhenItOpensUntilItIsDisposed()
    {
        var connections = new CompanionConnections();
        var device = Guid.NewGuid();

        var socket = connections.Opened(device);
        Assert.True(connections.IsOpen([device], Noon));

        socket.Dispose();
        Assert.False(connections.IsOpen([device], Noon));
    }

    [Fact]
    public void ADeviceWithTwoSocketsStaysOpenUntilBothEnd()
    {
        var connections = new CompanionConnections();
        var device = Guid.NewGuid();

        var first = connections.Opened(device);
        var second = connections.Opened(device);

        first.Dispose();
        first.Dispose();
        Assert.True(connections.IsOpen([device], Noon));

        second.Dispose();
        Assert.False(connections.IsOpen([device], Noon));
    }

    [Fact]
    public void APollCountsForSixtySeconds()
    {
        var connections = new CompanionConnections();
        var device = Guid.NewGuid();

        connections.Polled(device, Noon);

        Assert.True(connections.IsOpen([device], Noon.AddSeconds(60)));
        Assert.False(connections.IsOpen([device], Noon.AddSeconds(61)));
    }

    [Fact]
    public void AnyOneOfSeveralDevicesBeingOpenIsEnough()
    {
        var connections = new CompanionConnections();
        var running = Guid.NewGuid();
        using var socket = connections.Opened(running);

        Assert.True(connections.IsOpen([Guid.NewGuid(), running], Noon));
    }

    [Fact]
    public void ADevicesPlaceIsKnownForTenMinutesAndThenForgotten()
    {
        var locations = new DeviceLocations();
        var device = Guid.NewGuid();

        locations.Record(device, "39911", Noon, "wrld_cats");

        Assert.Equal(("39911", "wrld_cats"), locations.Where(device, Noon.AddMinutes(10)));
        Assert.Null(locations.Where(device, Noon.AddMinutes(10).AddSeconds(1)));
        Assert.Null(locations.Where(Guid.NewGuid(), Noon));
    }
}
