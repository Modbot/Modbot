namespace Modbot.My.Configuration;

/// <summary>
/// The environment variables Modbot.My reads, and nothing else.
/// </summary>
/// <remarks>
/// <c>DATABASE_URL</c> and <c>ROOT_API_KEY</c> are gone. my.modbot.co had a PostgreSQL database and
/// an <c>/admin</c> area from 2026-09-14 until 2026-09-16; both moved to Modbot Cloud (central
/// services spec 2.1.1).
/// </remarks>
/// <param name="CloudProxyUrl">
/// The Modbot Cloud this service reads from. Default <c>https://cloud.modbot.co</c>. Anything that is
/// not a full <c>http</c> or <c>https</c> address means the default.
/// </param>
/// <param name="CloudApiKey">
/// The key sent to Cloud. Required: without it there is nothing this service can show. Server-side
/// only — it is never returned by an endpoint, never logged, and never rendered into the page.
/// </param>
/// <param name="Port">The port to listen on. Railway injects it.</param>
public sealed record MyEnvironment(string? CloudProxyUrl, string? CloudApiKey, int Port)
{
    public const string CloudProxyUrlVariable = "MODBOT_CLOUD_PROXY_URL";
    public const string CloudApiKeyVariable = "MODBOT_CLOUD_API_KEY";
    public const string PortVariable = "PORT";
    public const int DefaultPort = 8080;

    public static MyEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        return new MyEnvironment(
            Blank(get(CloudProxyUrlVariable)),
            Blank(get(CloudApiKeyVariable)),
            port);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
