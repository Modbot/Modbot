using Modbot.Companion.Pairing;

namespace Modbot.Companion.CloudBackup;

/// <summary>
/// Where this client sends its event backup, and whether it sends one at all.
/// </summary>
/// <remarks>
/// <para><strong>Only this PC decides.</strong> Two places, both on the moderator's own machine:
/// the <c>cloud</c> object in <c>settings.json</c> (<c>"cloud": { "endpoint": "…", "disabled": true }</c>)
/// and the environment variables <c>MODBOT_CLOUD_ENDPOINT</c> and <c>MODBOT_CLOUD_DISABLED</c>. A
/// paired Modbot server has no say: an unpaired client and a client paired with any number of
/// servers send the same events to the same Cloud (cloud event backup spec 3.1).</para>
/// <para><strong>Precedence, for each of the two values:</strong> the environment variable, then
/// <c>settings.json</c>, then the default — on, to <c>https://cloud.modbot.co</c>.</para>
/// <list type="bullet">
/// <item>An endpoint that is not a full <c>https</c> address, or plain <c>http</c> to this PC, is
/// ignored and the default used. The rule is the same one pairing uses
/// (<see cref="ServerAddresses"/>), so events never cross a network in clear text.</item>
/// <item><c>MODBOT_CLOUD_DISABLED</c> set to <c>1</c>, <c>true</c>, <c>yes</c> or <c>on</c> turns the
/// backup off; <c>0</c>, <c>false</c>, <c>no</c> or <c>off</c> turns it on. Any other word is not
/// taken as either, and <c>settings.json</c> decides: a typo never turns sending back on.</item>
/// </list>
/// <para><strong>When it applies.</strong> Read once, when the client starts. Nothing here is
/// sent anywhere.</para>
/// </remarks>
/// <param name="Endpoint">The Modbot Cloud to send to.</param>
/// <param name="Disabled">True when nothing is sent to Modbot Cloud.</param>
/// <param name="RejectedEndpoint">An endpoint that was given but refused, kept only so the client can log it.</param>
public sealed record CloudSettings(Uri Endpoint, bool Disabled, string? RejectedEndpoint = null)
{
    public const string EndpointVariable = "MODBOT_CLOUD_ENDPOINT";

    public const string DisabledVariable = "MODBOT_CLOUD_DISABLED";

    public static readonly Uri DefaultEndpoint = new("https://cloud.modbot.co");

    public static CloudSettings Default { get; } = new(DefaultEndpoint, false);

    /// <summary>Works out the settings from the file's values and the environment.</summary>
    /// <param name="fileEndpoint"><c>cloud.endpoint</c> from <c>settings.json</c>, or null.</param>
    /// <param name="fileDisabled"><c>cloud.disabled</c> from <c>settings.json</c>, or null.</param>
    /// <param name="environment">Reads one environment variable; null when it is not set.</param>
    public static CloudSettings Resolve(string? fileEndpoint, bool? fileDisabled, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var disabled = Switch(environment(DisabledVariable)) ?? fileDisabled ?? false;

        var given = Blank(environment(EndpointVariable)) ?? Blank(fileEndpoint);
        if (given is null)
            return new CloudSettings(DefaultEndpoint, disabled);

        return Uri.TryCreate(given, UriKind.Absolute, out var endpoint)
               && endpoint.Scheme is "http" or "https"
               && ServerAddresses.IsAllowed(endpoint)
            ? new CloudSettings(endpoint, disabled)
            : new CloudSettings(DefaultEndpoint, disabled, given);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool? Switch(string? value) => Blank(value)?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };
}
