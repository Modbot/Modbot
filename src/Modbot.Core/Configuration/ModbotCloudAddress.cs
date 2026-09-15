namespace Modbot.Core.Configuration;

/// <summary>
/// Where this Modbot server talks to Modbot Cloud for its own purposes, or that it must not.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>MODBOT_CLOUD_ENDPOINT</c> and <c>MODBOT_CLOUD_DISABLED</c> (central services spec
/// 1.1). They are about this server only. Desktop clients paired with it are never told either value:
/// a client's own Cloud settings live on the moderator's PC, in its <c>settings.json</c> and
/// environment, and a server has no say in them.
/// </para>
/// <para>
/// What a server uses Cloud for, as planned in central services spec 1.1: usage reporting and
/// analytics (no account linking), sending its structured app logs (linking needed), downloading the
/// default term lists (linking needed), and downloading shared term lists, from another Modbot server
/// directly or through Cloud (the owner's account linking needed). None of those reads this yet;
/// each must honour <see cref="Disabled"/> when it does.
/// </para>
/// <para>
/// An endpoint that is not an absolute <c>http</c> or <c>https</c> address is ignored in favour of
/// the default.
/// </para>
/// </remarks>
/// <param name="Endpoint">The Cloud this server talks to.</param>
/// <param name="Disabled">This server sends nothing to, and fetches nothing from, Modbot Cloud.</param>
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
