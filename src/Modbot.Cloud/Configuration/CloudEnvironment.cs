namespace Modbot.Cloud.Configuration;

/// <summary>The environment variables Modbot Cloud reads, and nothing else.</summary>
/// <param name="DatabaseUrl">
/// Cloud's main database: installs, admin sessions and settings. A <c>postgres://</c> URL or a
/// keyword string. Required.
/// </param>
/// <param name="EngineDatabaseUrl">
/// The event storage: log lines, parsed events and their totals. A separate database, because it
/// is a hundred times the size of everything else and is pruned on its own schedule. Required.
/// </param>
/// <param name="RootApiKey">The key that unlocks <c>/admin</c>. When unset, admin refuses everyone.</param>
/// <param name="Port">The port to listen on. Railway injects it.</param>
public sealed record CloudEnvironment(string? DatabaseUrl, string? EngineDatabaseUrl, string? RootApiKey, int Port)
{
    public const string DatabaseUrlVariable = "DATABASE_URL";
    public const string EngineDatabaseUrlVariable = "DATABASE_ENGINE_URL";
    public const string RootApiKeyVariable = "ROOT_API_KEY";
    public const string PortVariable = "PORT";
    public const int DefaultPort = 8080;

    public static CloudEnvironment Read(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var port = int.TryParse(get(PortVariable), out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        return new CloudEnvironment(
            Blank(get(DatabaseUrlVariable)),
            Blank(get(EngineDatabaseUrlVariable)),
            Blank(get(RootApiKeyVariable)),
            port);
    }

    /// <summary>
    /// One sentence per missing database variable, naming it. Empty when both are set.
    /// </summary>
    /// <remarks>
    /// Both are required, and both are named when both are missing, so a first deploy on Railway is
    /// fixed in one pass rather than two.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (DatabaseUrl is null)
        {
            problems.Add(
                $"{DatabaseUrlVariable} is not set. Modbot Cloud needs its main PostgreSQL database; "
                + "the expected form is postgres://user:password@host:5432/database.");
        }

        if (EngineDatabaseUrl is null)
        {
            problems.Add(
                $"{EngineDatabaseUrlVariable} is not set. Modbot Cloud needs a second PostgreSQL database "
                + "for event storage; the expected form is postgres://user:password@host:5432/database.");
        }

        return problems;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
