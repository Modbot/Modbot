using Microsoft.AspNetCore.Http;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.Api.Features.Onboarding.Integrations;

/// <param name="BotToken">
/// The Discord bot token. Encrypted at rest (spec 8.3). Null leaves whatever is stored alone;
/// empty clears it, which is how the bot is turned off.
/// </param>
/// <param name="GuildId">The guild the bot serves. An opaque snowflake; never parsed.</param>
/// <param name="InstanceChannelId">
/// The channel open instances are announced in, which is a notice board for members rather than a
/// record for the team -- so it is usually a different channel from the event channels. Null
/// leaves it alone; empty clears it and stops announcing.
/// </param>
/// <param name="InstanceMessage">
/// The operator's own line, posted above each card. Null leaves it alone; empty clears it and
/// posts the card on its own. Always sent with mentions disabled.
/// </param>
/// <param name="InstanceShowNames">
/// Whether a card lists who is in an instance while a moderator is watching it. Null leaves it alone.
/// </param>
public sealed record DiscordSettings(
    string? BotToken = null,
    string? GuildId = null,
    string? InstanceChannelId = null,
    string? InstanceMessage = null,
    bool? InstanceShowNames = null);

/// <param name="Password">Same null-versus-empty rule as the Discord token.</param>
/// <param name="UseTls">
/// Defaults to true when a host is supplied and nothing says otherwise. A relay that needs it off
/// is the unusual one, and defaulting the other way would silently send credentials in the clear.
/// </param>
public sealed record SmtpSettings(
    string? Host = null,
    int? Port = null,
    string? Username = null,
    string? Password = null,
    string? FromAddress = null,
    bool? UseTls = null);

/// <param name="PublicAddress">
/// The address people use to reach this Modbot. Null leaves it alone; empty clears it. The only
/// thing an emailed or messaged link is ever built from, which is why a person confirms it here
/// rather than the server inferring it from a request (accounts and access design §4.2).
/// </param>
public sealed record IntegrationsRequest(
    DiscordSettings? Discord = null,
    SmtpSettings? Smtp = null,
    string? PublicAddress = null);

public sealed record IntegrationsResponse(bool DiscordConfigured, bool SmtpConfigured, string? PublicAddress);

/// <summary>
/// Spec 7.1 step 5: Discord and SMTP, both genuinely optional.
/// </summary>
/// <remarks>
/// <para>
/// Skippable means skippable. A deployment with neither configured is a complete, working Modbot:
/// the Discord bot simply does not start (spec 9) and notifications degrade to the surfaces that
/// remain (spec 4.5.3). Nothing here may become a precondition for finishing setup.
/// </para>
/// <para>
/// Nothing is validated by connecting. A Discord token is checked by starting a gateway session
/// and SMTP by sending mail, and both are slow, both fail for reasons unrelated to the value
/// being wrong, and neither is worth blocking the last step of a wizard on. The VRChat step
/// validates live because Modbot cannot function at all without it; these two can.
/// </para>
/// </remarks>
public static class IntegrationsHandler
{
    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IntegrationsRequest? request,
        ModbotContext db,
        ISecretProtector protector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);

        var settings = await db.GetSettingsAsync(ct);
        var before = Snapshot.Of(settings);

        if (request?.Discord is { } discord)
        {
            if (discord.BotToken is not null)
            {
                settings.DiscordBotTokenEncrypted = discord.BotToken.Trim().Length == 0
                    ? null
                    : protector.Protect(discord.BotToken.Trim());
            }

            if (discord.GuildId is not null)
            {
                settings.DiscordGuildId = discord.GuildId.Trim().Length == 0
                    ? null
                    : discord.GuildId.Trim();
            }

            if (discord.InstanceChannelId is not null)
            {
                settings.DiscordInstanceChannelId =
                    discord.InstanceChannelId.Trim().Length == 0 ? null : discord.InstanceChannelId.Trim();
            }

            if (discord.InstanceMessage is not null)
            {
                var message = discord.InstanceMessage.Trim();

                // Discord refuses a message body over 2,000 characters, and the refusal would
                // arrive in a log twenty minutes later rather than under the box being typed in.
                if (message.Length > 2000)
                {
                    return Results.BadRequest(new
                    {
                        error = "That message is over Discord's 2,000-character limit.",
                    });
                }

                settings.DiscordInstanceMessage = message.Length == 0 ? null : message;
            }

            if (discord.InstanceShowNames is { } showNames)
                settings.DiscordInstanceShowNames = showNames;
        }

        if (request?.Smtp is { } smtp)
        {
            if (smtp.Host is not null)
                settings.SmtpHost = smtp.Host.Trim().Length == 0 ? null : smtp.Host.Trim();

            if (smtp.Port is { } port)
            {
                if (port is < 1 or > 65535)
                    return Results.BadRequest(new { error = "That SMTP port is not a port number." });

                settings.SmtpPort = port;
            }

            if (smtp.Username is not null)
                settings.SmtpUsername = smtp.Username.Trim().Length == 0 ? null : smtp.Username.Trim();

            if (smtp.Password is not null)
            {
                settings.SmtpPasswordEncrypted = smtp.Password.Length == 0
                    ? null
                    : protector.Protect(smtp.Password);
            }

            if (smtp.FromAddress is not null)
            {
                settings.SmtpFromAddress =
                    smtp.FromAddress.Trim().Length == 0 ? null : smtp.FromAddress.Trim();
            }

            if (smtp.UseTls is { } useTls)
                settings.SmtpUseTls = useTls;

            // A host with no port is the commonest way to end up with mail that never sends.
            // 587 is submission-with-STARTTLS, which is what almost every relay wants.
            if (settings.SmtpHost is { Length: > 0 } && settings.SmtpPort is null)
                settings.SmtpPort = 587;
        }

        if (request?.PublicAddress is { } typed)
        {
            var (address, error) = Modbot.Core.Configuration.PublicAddress.Normalize(typed);
            if (error is not null)
                return Results.BadRequest(new { error });

            settings.PublicAddress = address;
        }

        // The settings screens save through this step once setup is done, so it is recorded like
        // any other settings save: the bot token and the SMTP password say only that they changed.
        var change = Changes(before, settings);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await change.RecordAfterSetupAsync(http, ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new IntegrationsResponse(
            settings.DiscordBotTokenEncrypted is not null,
            settings.SmtpHost is { Length: > 0 },
            settings.PublicAddress));
    }

    /// <summary>What this step can change, for telling afterwards what it did.</summary>
    private readonly record struct Snapshot(
        string? BotToken,
        string? GuildId,
        string? InstanceChannelId,
        string? InstanceMessage,
        bool InstanceShowNames,
        string? SmtpHost,
        int? SmtpPort,
        string? SmtpUsername,
        string? SmtpPassword,
        string? SmtpFromAddress,
        bool SmtpUseTls,
        string? PublicAddress)
    {
        public static Snapshot Of(Core.Data.Entities.Settings s) => new(
            s.DiscordBotTokenEncrypted,
            s.DiscordGuildId,
            s.DiscordInstanceChannelId,
            s.DiscordInstanceMessage,
            s.DiscordInstanceShowNames,
            s.SmtpHost,
            s.SmtpPort,
            s.SmtpUsername,
            s.SmtpPasswordEncrypted,
            s.SmtpFromAddress,
            s.SmtpUseTls,
            s.PublicAddress);
    }

    private static SettingsChange Changes(Snapshot was, Core.Data.Entities.Settings now)
    {
        var after = Snapshot.Of(now);

        return new SettingsChange("integrations")
            .Secret("discordBotToken", !string.Equals(was.BotToken, after.BotToken, StringComparison.Ordinal))
            .Field("discordGuildId", was.GuildId, after.GuildId)
            .Field("instanceChannelId", was.InstanceChannelId, after.InstanceChannelId)
            .Field("instanceMessage", was.InstanceMessage, after.InstanceMessage)
            .Field("instanceShowNames", was.InstanceShowNames, after.InstanceShowNames)
            .Field("smtpHost", was.SmtpHost, after.SmtpHost)
            .Field("smtpPort", was.SmtpPort, after.SmtpPort)
            .Field("smtpUsername", was.SmtpUsername, after.SmtpUsername)
            .Secret("smtpPassword", !string.Equals(was.SmtpPassword, after.SmtpPassword, StringComparison.Ordinal))
            .Field("smtpFromAddress", was.SmtpFromAddress, after.SmtpFromAddress)
            .Field("smtpUseTls", was.SmtpUseTls, after.SmtpUseTls)
            .Field("publicAddress", was.PublicAddress, after.PublicAddress);
    }
}
