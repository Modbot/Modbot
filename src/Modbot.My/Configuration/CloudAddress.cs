namespace Modbot.My.Configuration;

/// <summary>
/// The Modbot Cloud this service reads from, and the key it reads with.
/// </summary>
/// <remarks>
/// <para>
/// From <c>MODBOT_CLOUD_PROXY_URL</c> and <c>MODBOT_CLOUD_API_KEY</c>. my.modbot.co has no database
/// of its own: everything it shows comes from Cloud (central services spec 2.1.1).
/// </para>
/// <para>
/// <strong>The key is server-side only.</strong> It is never returned by an endpoint, never written
/// to a log, and never reaches a browser.
/// </para>
/// </remarks>
/// <param name="Endpoint">Always ends in a slash, so a relative path appends rather than replaces.</param>
/// <param name="ApiKey">Sent to Cloud as <c>Authorization: Bearer</c>. Null means Cloud refuses us.</param>
public sealed record CloudAddress(Uri Endpoint, string? ApiKey)
{
    public const string DefaultEndpoint = "https://cloud.modbot.co/";

    public static CloudAddress From(MyEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var endpoint = Uri.TryCreate(environment.CloudProxyUrl, UriKind.Absolute, out var parsed)
                       && parsed.Scheme is "http" or "https"
            ? parsed
            : new Uri(DefaultEndpoint);

        // A base address without a trailing slash drops its last segment when a relative path is
        // appended, which would quietly send every call to the wrong place.
        if (!endpoint.AbsoluteUri.EndsWith('/'))
            endpoint = new Uri(endpoint.AbsoluteUri + "/");

        return new CloudAddress(endpoint, environment.CloudApiKey);
    }
}
