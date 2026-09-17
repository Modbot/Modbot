namespace Modbot.Core.Configuration;

/// <summary>
/// The complete set of environment variables Modbot reads. There are three.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec section 2.6: configuration lives in the database, not the environment. VRChat
/// credentials, the managed group, the egress proxy, Discord and SMTP are all entered through the
/// onboarding wizard and stored in <c>Settings</c>.
/// </para>
/// <para>
/// These three are the exceptions, and each earns it by being needed <em>before</em> the database is
/// reachable:
/// </para>
/// <list type="bullet">
///   <item><c>PORT</c> — supplied by the host; you cannot serve the wizard without it.</item>
///   <item><c>DATABASE_URL</c> — how to reach the database that holds everything else.</item>
///   <item><c>SEQ_URL</c> — optional. It is how you debug a deployment that <em>cannot</em> reach
///   its database, which is exactly when database-stored config is no help.</item>
/// </list>
/// <para><strong>Do not add a fourth without the same justification.</strong></para>
/// <para>
/// <c>MODBOT_CLOUD_ENDPOINT</c> and <c>MODBOT_CLOUD_DISABLED</c> are the maintainer's exception, not
/// a fourth setting in that sense (central services spec 1.1). They are not about reaching the
/// database; they say where this server talks to Modbot Cloud for its own purposes, and whether it
/// does, and are read from the environment because that is where the maintainer asked operators to
/// set them. They say nothing to paired companions, which keep their own Cloud settings. Both
/// are optional, and Modbot runs identically without them.
/// </para>
/// <para>
/// <c>MODBOT_DEMO</c> and <c>MODBOT_DEMO_RESET_HOURS</c> are the same kind of exception, for the
/// same reason (demo mode design §2). Demo mode decides whether there is a sign-in page at all, so
/// it cannot be a setting inside an app that has no way to sign in to it; and the whole point of
/// the switch is that a real deployment can never turn it on by accident, which a row in the
/// database somebody can edit would not give.
/// </para>
/// </remarks>
public sealed class ModbotEnvironment
{
    public const string PortVariable = "PORT";
    public const string DatabaseUrlVariable = "DATABASE_URL";
    public const string SeqUrlVariable = "SEQ_URL";
    public const string CloudEndpointVariable = "MODBOT_CLOUD_ENDPOINT";
    public const string CloudDisabledVariable = "MODBOT_CLOUD_DISABLED";
    public const string MyUrlVariable = "MODBOT_MY_URL";

    /// <summary>Where my.modbot.co is, when nothing says otherwise.</summary>
    public const string DefaultMyUrl = "https://my.modbot.co";

    /// <summary>Port to listen on. Defaults to 8080, which is what Railway and most hosts expect.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>PostgreSQL connection, as a URL or an ADO.NET connection string.</summary>
    public string? DatabaseUrl { get; init; }

    /// <summary>Optional Seq endpoint. Null means the sink is not registered.</summary>
    public string? SeqUrl { get; init; }

    /// <summary>True when <c>MODBOT_DEBUG_LOGGING</c> is set truthy — enables the Debug streams.</summary>
    public bool DebugLogging { get; init; }

    /// <summary>
    /// The Modbot Cloud this server talks to, from <c>MODBOT_CLOUD_ENDPOINT</c>. Null means the
    /// default, <c>https://cloud.modbot.co</c>. See <see cref="ModbotCloudAddress"/>.
    /// </summary>
    public string? CloudEndpoint { get; init; }

    /// <summary>True when <c>MODBOT_CLOUD_DISABLED</c> is set truthy: this server does not talk to Modbot Cloud.</summary>
    public bool CloudDisabled { get; init; }

    /// <summary>
    /// Where my.modbot.co is, from <c>MODBOT_MY_URL</c>, or the default. It is only ever a link this
    /// server offers a person, never something it calls.
    /// </summary>
    /// <remarks>
    /// A group that runs its own selector points every link at it with this one variable. Anything
    /// that is not an absolute <c>http</c> or <c>https</c> address means the default.
    /// </remarks>
    public string MyUrl { get; init; } = DefaultMyUrl;

    /// <summary>
    /// True when <c>MODBOT_DEMO</c> is set truthy. Asks for demo mode; it is not granted here.
    /// <see cref="DemoMode"/> is the only thing that decides whether demo mode is on.
    /// </summary>
    public bool Demo { get; init; }

    /// <summary>
    /// <c>MODBOT_DEMO_RESET_HOURS</c>: hours between automatic demo resets, 0 for never. Null when
    /// unset or not a whole number from 0 to 8760, which means the default.
    /// </summary>
    public int? DemoResetHours { get; init; }

    public static ModbotEnvironment Read(IDictionary<string, string?>? source = null)
    {
        string? Get(string key) => source is not null
            ? source.TryGetValue(key, out var v) ? v : null
            : Environment.GetEnvironmentVariable(key);

        var rawPort = Get(PortVariable);
        var rawResetHours = Get(DemoMode.ResetHoursVariable);

        return new ModbotEnvironment
        {
            Port = int.TryParse(rawPort, out var port) && port is > 0 and <= 65535 ? port : 8080,
            DatabaseUrl = Blank(Get(DatabaseUrlVariable)),
            SeqUrl = Blank(Get(SeqUrlVariable)),
            DebugLogging = Truthy(Get("MODBOT_DEBUG_LOGGING")),
            CloudEndpoint = Blank(Get(CloudEndpointVariable)),
            CloudDisabled = Truthy(Get(CloudDisabledVariable)),
            MyUrl = Address(Get(MyUrlVariable)) ?? DefaultMyUrl,
            Demo = Truthy(Get(DemoMode.Variable)),

            // A year of hours is the ceiling. Anything outside it -- or not a number at all -- is
            // somebody's typo, and a typo should leave the default in place rather than turn the
            // reset off or set it a century away.
            DemoResetHours = int.TryParse(rawResetHours, out var hours) && hours is >= 0 and <= 8760
                ? hours
                : null,
        };

        static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        /// <summary>An absolute http or https address with no trailing slash, or null.</summary>
        static string? Address(string? v) =>
            Uri.TryCreate(Blank(v), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/')
                : null;

        static bool Truthy(string? v) =>
            v is not null && v.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    /// <summary>
    /// Returns a human-readable problem, or null when the environment is usable.
    /// </summary>
    /// <remarks>
    /// Missing configuration is reported as one clear sentence naming the variable, because the
    /// person reading it is looking at a Railway dashboard rather than a stack trace.
    /// </remarks>
    public string? Validate() => DatabaseUrl is null
        ? $"{DatabaseUrlVariable} is not set. Modbot needs a PostgreSQL connection string; "
          + "on Railway, add a Postgres service and reference its connection URL."
        : null;
}
