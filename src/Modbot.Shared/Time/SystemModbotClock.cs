namespace Modbot.Core.Time;

/// <summary>
/// The one place in Modbot permitted to read the machine clock.
/// </summary>
/// <remarks>
/// <c>NoSystemClockTests</c> scans the source tree and fails the build if any other file reads
/// <c>DateTime.UtcNow</c> and friends. If you need the time somewhere else, inject
/// <see cref="IModbotClock"/>.
/// </remarks>
public sealed class SystemModbotClock : IModbotClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
