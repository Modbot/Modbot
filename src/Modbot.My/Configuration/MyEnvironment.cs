namespace Modbot.My.Configuration;

/// <summary>The environment variables Modbot.My reads, and nothing else.</summary>
/// <param name="DatabaseUrl">PostgreSQL, as a <c>postgres://</c> URL or a keyword string. Required.</param>
/// <param name="RootApiKey">
/// The key that unlocks every endpoint that reads the registry. When unset, those endpoints refuse
/// everyone.
/// </param>
/// <param name="CloudProxyUrl">
/// The Modbot Cloud this service reads from. Default <c>https://cloud.modbot.co</c>. Anything that is
/// not a full <c>http</c> or <c>https</c> address means the default.
/// </param>
/// <param name="CloudApiKey">
/// The key sent to Cloud. Server-side only: it is never returned by an endpoint and never reaches a
/// browser.
/// </param>
/// <param name="Port">The port to listen on. Railway injects it.</param>
public sealed record MyEnvironment(
    string? DatabaseUrl,
    string? RootApiKey,
    string? CloudProxyUrl,
    string? CloudApiKey,
    int Port)
{
    public const string DatabaseUrlVariable = "DATABASE_URL";
    public const string RootApiKeyVariable = "ROOT_API_KEY";
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
            Blank(get(DatabaseUrlVariable)),
            Blank(get(RootApiKeyVariable)),
            Blank(get(CloudProxyUrlVariable)),
            Blank(get(CloudApiKeyVariable)),
            port);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
