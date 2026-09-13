using Modbot.VRChat.Scheduling;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>Elapsed time a test controls, standing in for the process stopwatch.</summary>
public sealed class FakeMonotonicClock : IMonotonicClock
{
    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan by) => Elapsed += by;
}
