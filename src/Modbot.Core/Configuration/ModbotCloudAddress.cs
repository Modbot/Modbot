namespace Modbot.Core.Configuration;

/// <summary>
/// Where this deployment's paired desktop clients send their log backup, or that they must not.
/// </summary>
/// <remarks>
/// <para>
/// The client backs up every VRChat log line it reads to Modbot Cloud unless its moderator turns
/// that off (cloud log backup spec 3.1). A group's operator decides where that goes for the
/// clients paired with their server: <c>MODBOT_CLOUD_ENDPOINT</c> names another Cloud, and
/// <c>MODBOT_CLOUD_DISABLED=1</c> stops their clients sending at all. The server itself sends
/// nothing to Cloud; it only tells its clients, on <c>GET /api/v{n}/client/time</c>.
/// </para>
/// <para>
/// An endpoint that is not an absolute <c>http</c> or <c>https</c> address is ignored in favour of
/// the default. The client refuses plain <c>http</c> to anywhere but itself, so a mistyped address
/// costs that operator's clients their backup rather than sending it somewhere unintended.
/// </para>
/// </remarks>
/// <param name="Endpoint">The Cloud clients send to.</param>
/// <param name="Disabled">Clients paired with this server send nothing.</param>
public sealed record ModbotCloudAddress(Uri Endpoint, bool Disabled)
{
    public static readonly Uri DefaultEndpoint = new("https://cloud.modbot.co");

    public static ModbotCloudAddress Default { get; } = new(DefaultEndpoint, false);

    public static ModbotCloudAddress From(ModbotEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var endpoint = Uri.TryCreate(environment.CloudEndpoint, UriKind.Absolute, out var parsed)
                       && parsed.Scheme is "http" or "https"
            ? parsed
            : DefaultEndpoint;

        return new ModbotCloudAddress(endpoint, environment.CloudDisabled);
    }
}
