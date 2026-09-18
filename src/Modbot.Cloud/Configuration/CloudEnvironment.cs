namespace Modbot.Cloud.Configuration;

/// <summary>The environment variables Modbot Cloud reads, and nothing else.</summary>
/// <param name="DatabaseUrl">
/// Cloud's main database: accounts, servers, installs, admin sessions and settings. A
/// <c>postgres://</c> URL or a keyword string. Required.
/// </param>
/// <param name="EngineDatabaseUrl">
/// The event storage: backed-up presence events and their totals. A separate database, because it
/// grows with every client and is pruned on its own schedule. Required.
/// </param>
/// <param name="RootApiKey">The key that unlocks <c>/admin</c>. When unset, admin refuses everyone.</param>
/// <param name="InstancesApiKey">
/// The key that reads the public instances feed — the landing page's key. Read-only and worth far less
/// than the root key, which is the point of it being separate. When unset, only the root key opens
/// the feed; when both are unset the feed refuses everyone.
/// </param>
/// <param name="ProxyApiKey">
/// The key my.modbot.co and the landing page send. It opens the narrow read and save endpoints under
/// <c>/api/v1/site</c> and nothing else. When unset, those refuse everyone.
/// </param>
/// <param name="ResendApiKey">
/// The Resend key Cloud sends mail with. When unset, Cloud sends no mail, and registering, verifying
/// and resetting a password are refused rather than silently swallowed.
/// </param>
/// <param name="MailFrom">The From address on Cloud's mail, such as <c>Modbot &lt;noreply@modbot.co&gt;</c>.</param>
/// <param name="PublicUrl">Where Cloud is reachable, for the links in its mail.</param>
/// <param name="Port">The port to listen on. Railway injects it.</param>
/// <param name="GitHubToken">
/// Reads the repository's contributors for the showcase. Not needed for a public repository; needed
/// while it is private, and without it the contributor list is simply empty.
/// </param>
/// <param name="GitHubRepository">The repository the contributors come from, as <c>owner/name</c>.</param>
/// <param name="GitHubReleasesRepository">
/// The repository the releases come from, as <c>owner/name</c>. Usually the same one; separate
/// because the client's releases may be published somewhere the public can download them from
/// while the source repository is private.
/// </param>
/// <param name="DockerImage">
/// The server's image on Docker Hub, as <c>owner/name</c>. Cloud reads its tags so it can tell an
/// operator what to pull.
/// </param>
public sealed record CloudEnvironment(
    string? DatabaseUrl,
    string? EngineDatabaseUrl,
    string? RootApiKey,
    string? InstancesApiKey,
    string? ProxyApiKey,
    string? ResendApiKey,
    string? MailFrom,
    string? PublicUrl,
    int Port,
    string? GitHubToken = null,
    string? GitHubRepository = null,
    string? GitHubReleasesRepository = null,
    string? DockerImage = null)
{
    public const string DatabaseUrlVariable = "DATABASE_URL";
    public const string EngineDatabaseUrlVariable = "DATABASE_ENGINE_URL";
    public const string RootApiKeyVariable = "ROOT_API_KEY";
    public const string InstancesApiKeyVariable = "INSTANCES_API_KEY";

    /// <summary>The variable's name before 2026-09-17. Still read, so a deployment set up under it keeps working.</summary>
    public const string OldInstancesApiKeyVariable = "ROOMS_API_KEY";
    public const string ProxyApiKeyVariable = "PROXY_API_KEY";
    public const string ResendApiKeyVariable = "RESEND_API_KEY";
    public const string MailFromVariable = "MAIL_FROM";
    public const string PublicUrlVariable = "CLOUD_PUBLIC_URL";
    public const string PortVariable = "PORT";

    /// <summary>Reads the repository's contributors. Optional; without it the list is empty.</summary>
    public const string GitHubTokenVariable = "GITHUB_TOKEN";

    /// <summary>The repository the contributors come from. Defaults to the project's own.</summary>
    public const string GitHubRepositoryVariable = "GITHUB_REPOSITORY";

    /// <summary>
    /// The repository the releases come from. Defaults to <see cref="GitHubRepositoryVariable"/>,
    /// then to the project's own.
    /// </summary>
    public const string GitHubReleasesRepositoryVariable = "GITHUB_RELEASES_REPOSITORY";

    /// <summary>The server's image on Docker Hub. Optional; without it the project's own is read.</summary>
    public const string DockerImageVariable = "DOCKER_IMAGE";
    public const int DefaultPort = 8080;

    public const string DefaultPublicUrl = "https://cloud.modbot.co";

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
            Blank(get(InstancesApiKeyVariable)) ?? Blank(get(OldInstancesApiKeyVariable)),
            Blank(get(ProxyApiKeyVariable)),
            Blank(get(ResendApiKeyVariable)),
            Blank(get(MailFromVariable)),
            Blank(get(PublicUrlVariable)),
            port,
            Blank(get(GitHubTokenVariable)),
            Blank(get(GitHubRepositoryVariable)),
            Blank(get(GitHubReleasesRepositoryVariable)) ?? Blank(get(GitHubRepositoryVariable)),
            Blank(get(DockerImageVariable)));
    }

    /// <summary>
    /// The address in <see cref="PublicUrl"/>, or the default when it is unset or not an absolute
    /// <c>http</c> or <c>https</c> address.
    /// </summary>
    public Uri PublicAddress =>
        Uri.TryCreate(PublicUrl, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
            ? parsed
            : new Uri(DefaultPublicUrl);

    /// <summary>
    /// One sentence per problem, naming the variable. Empty when Cloud can start.
    /// </summary>
    /// <remarks>
    /// Both databases are required, and both are named when both are missing, so a first deploy on
    /// Railway is fixed in one pass rather than two. A Resend key with no From address is the third:
    /// it would start, and then fail on the first mail somebody was waiting for.
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

        if (ResendApiKey is not null && MailFrom is null)
        {
            problems.Add(
                $"{MailFromVariable} is not set, but {ResendApiKeyVariable} is. Modbot Cloud needs an "
                + "address to send from, such as \"Modbot <noreply@modbot.co>\".");
        }

        return problems;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
