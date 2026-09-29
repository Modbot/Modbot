namespace Modbot.Core.Logging;

/// <summary>
/// The property every log event carries to say which stream it belongs to.
/// </summary>
/// <remarks>
/// One property, three filters — see <see cref="ModbotLogging"/>. Routing by a property rather than
/// by separate logger instances means there is nothing to keep in sync when a new call site appears:
/// an event without an area simply lands in Main, which is the right default.
/// </remarks>
public static class LogArea
{
    /// <summary>The Serilog property name. Enrich with this to route an event.</summary>
    public const string Name = "LogArea";

    /// <summary>
    /// Outbound API traffic — VRChat and Discord. Routed to its own stream so rate-limit
    /// forensics are a query over API calls rather than a grep through application noise.
    /// </summary>
    public const string Http = "Http";

    /// <summary>Background sync jobs.</summary>
    public const string Sync = "Sync";

    /// <summary>Moderation actions and accountability.</summary>
    public const string Moderation = "Moderation";

    /// <summary>Fact writing, daily totals, retention.</summary>
    public const string Analytics = "Analytics";

    /// <summary>Onboarding, settings, auth.</summary>
    public const string Setup = "Setup";

    /// <summary>The Discord bot: connection, commands, channel posting.</summary>
    public const string Discord = "Discord";

    /// <summary>
    /// Printed to the console and nowhere else: not the log files, not Seq, and not the database
    /// log, which is sent on to Modbot Cloud. For the setup code, which only whoever can read this
    /// server's console should see (first-run setup code design §2).
    /// </summary>
    public const string ConsoleOnly = "ConsoleOnly";
}
