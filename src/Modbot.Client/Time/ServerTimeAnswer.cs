namespace Modbot.Client.Time;

/// <summary>
/// What a paired server said about where this client's event backup goes.
/// </summary>
/// <param name="Endpoint">
/// The Modbot Cloud to send to, or null when the server named none: an older server, or one that
/// disabled it.
/// </param>
/// <param name="Disabled">The server's operator turned the backup off for their clients.</param>
/// <param name="InstanceId">The server's own id, passed on to Cloud when there is one.</param>
public sealed record ServerCloudAnswer(Uri? Endpoint, bool Disabled, string? InstanceId)
{
    /// <summary>An older server's answer, which has no <c>cloud</c> object: no preference.</summary>
    public static ServerCloudAnswer NoPreference { get; } = new(null, false, null);
}

/// <summary>One time probe's result: the clock measurement, and the Cloud answer that rode with it.</summary>
public sealed record ServerTimeAnswer(ClockSample Sample, ServerCloudAnswer Cloud);
