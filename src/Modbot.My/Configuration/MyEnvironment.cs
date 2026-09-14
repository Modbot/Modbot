namespace Modbot.My.Configuration;

/// <summary>The environment variables Modbot.My reads, and nothing else.</summary>
/// <param name="DatabaseUrl">PostgreSQL, as a <c>postgres://</c> URL or a keyword string. Required.</param>
/// <param name="RootApiKey">
/// The key that unlocks every endpoint that reads the registry. When unset, those endpoints refuse
/// everyone.
/// </param>
/// <param name="Port">The port to listen on. Railway injects it.</param>
public sealed record MyEnvironment(string? DatabaseUrl, string? RootApiKey, int Port)
{
    public const string DatabaseUrlVariable = "DATABASE_URL";
    public const string RootApiKeyVariable = "ROOT_API_KEY";
    public const string PortVariable = "PORT";
    public const int DefaultPort = 8080;

    public static MyEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        return new MyEnvironment(Blank(get(DatabaseUrlVariable)), Blank(get(RootApiKeyVariable)), port);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
