using Modbot.Client.Ingest;
using Modbot.Client.Pairing;

namespace Modbot.Client.CloudBackup;

/// <summary>Whether the backup may send, may only queue, or must do nothing.</summary>
public enum CloudDestinationKind
{
    /// <summary>Send to <see cref="CloudDestination.Endpoint"/>.</summary>
    Send,

    /// <summary>A paired server has not said yet. Queue, and do not send.</summary>
    Wait,

    /// <summary>A paired server's operator turned the backup off. Queue nothing, send nothing.</summary>
    TurnedOffByServer,
}

/// <summary>
/// Where the event backup goes right now, worked out from the paired servers' answers.
/// </summary>
/// <remarks>
/// <para>The rule (cloud event backup spec 3.1), in order:</para>
/// <list type="number">
/// <item>No paired server: the default Cloud.</item>
/// <item>Any paired server said it is off: nothing is sent, by anyone's say-so. One operator's
/// opt-out is not overruled by another server the moderator also staffs.</item>
/// <item>A paired server has not answered yet: wait. A server that turns the backup off never has
/// its moderators send a first batch before the client learns that.</item>
/// <item>Otherwise the first paired server that named an address. One that named none — an older
/// server — has no preference, and the default is used.</item>
/// </list>
/// <para>A paused or stopped pairing is not asked, so it neither holds sending nor decides it.
/// An address that fails the client's HTTPS rule counts as off.</para>
/// </remarks>
public sealed record CloudDestination(CloudDestinationKind Kind, Uri? Endpoint, string? ModbotServerId)
{
    public static readonly Uri DefaultEndpoint = new("https://cloud.modbot.co");

    public static CloudDestination Default { get; } = new(CloudDestinationKind.Send, DefaultEndpoint, null);

    public static CloudDestination Resolve(IEnumerable<ServerConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var asked = connections
            .Where(c => !c.IsPaused && c.State is not ConnectionState.Stopped)
            .ToList();

        if (asked.Count == 0)
            return Default;

        var answers = asked.Select(c => c.Cloud).ToList();

        if (answers.Any(a => a is { Disabled: true } || a?.Endpoint is { } e && !ServerAddresses.IsAllowed(e)))
            return new CloudDestination(CloudDestinationKind.TurnedOffByServer, null, null);

        if (answers.Any(a => a is null))
            return new CloudDestination(CloudDestinationKind.Wait, null, null);

        var named = answers.FirstOrDefault(a => a!.Endpoint is not null);
        var serverId = answers.Select(a => a!.InstanceId).FirstOrDefault(id => id is not null);

        return new CloudDestination(CloudDestinationKind.Send, named?.Endpoint ?? DefaultEndpoint, serverId);
    }
}
