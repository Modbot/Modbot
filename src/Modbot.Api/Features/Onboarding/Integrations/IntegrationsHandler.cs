using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.Api.Features.Onboarding.Integrations;

/// <param name="BotToken">
/// The Discord bot token. Encrypted at rest (spec 8.3). Null leaves whatever is stored alone;
/// empty clears it, which is how the bot is turned off.
/// </param>
/// <param name="GuildId">The guild the bot serves. An opaque snowflake; never parsed.</param>
public sealed record DiscordSettings(string? BotToken = null, string? GuildId = null);

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

public sealed record IntegrationsRequest(DiscordSettings? Discord = null, SmtpSettings? Smtp = null);

public sealed record IntegrationsResponse(bool DiscordConfigured, bool SmtpConfigured);

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
        IntegrationsRequest? request,
        ModbotContext db,
        ISecretProtector protector,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);

        var settings = await db.GetSettingsAsync(ct);

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

        await db.SaveChangesAsync(ct);

        return Results.Ok(new IntegrationsResponse(
            settings.DiscordBotTokenEncrypted is not null,
            settings.SmtpHost is { Length: > 0 }));
    }
}
