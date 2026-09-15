using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Onboarding.Status;

public static class StatusHandler
{
    public static async Task<IResult> HandleAsync(
        ModbotContext db,
        UserAccountService accounts,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(http);

        var hasAdministrator = await accounts.AnyUsersAsync(ct);
        var settings = await db.GetSettingsAsync(ct);

        var vrchat = new VRChatAccountStatus(
            settings.VRChatUsername,
            settings.VRChatDisplayName,
            settings.VRChatVerifiedAt);

        var connection = new ConnectionStatus(
            settings.ConnectionCheckedAt,
            settings.ProxyUrl,
            settings.ProxyUsername,
            settings.ProxyPasswordEncrypted is not null);

        var group = settings.ManagedGroupId is { Length: > 0 } id
            ? new ManagedGroupStatus(id, settings.ManagedGroupName ?? id)
            : null;

        // Optional: a host that maps the API without describing its deployment simply has no
        // suggestion to offer.
        var deployment = http.RequestServices.GetService(typeof(Modbot.Core.Configuration.DeploymentInfo))
            as Modbot.Core.Configuration.DeploymentInfo;

        var logEvents = Modbot.Core.Discord.ModerationLogEvents.Parse(settings.DiscordLogEventTypes);

        var integrations = new IntegrationStatus(
            settings.DiscordBotTokenEncrypted is not null,
            settings.DiscordGuildId,
            settings.DiscordLogChannelId,
            Modbot.Core.Discord.ModerationLogEvents.Allowed.Where(logEvents.Contains).ToList(),
            Modbot.Core.Discord.ModerationLogEvents.Allowed
                .Select(t => new DiscordLogEventChoice(t, Modbot.Api.Features.Audit.FactLabels.For(t)))
                .ToList(),
            settings.DiscordInstanceChannelId,
            settings.DiscordInstanceMessage,
            settings.DiscordInstanceShowNames,
            settings.SmtpHost is { Length: > 0 },
            settings.SmtpHost,
            settings.PublicAddress,
            deployment?.PublicAddressSuggestion);

        var authenticated = http.User.Identity?.IsAuthenticated == true;

        // From the claim the session check keeps current, so this costs no second read.
        var linked = authenticated && Modbot.Api.Auth.ModbotAuth.IsVRChatLinked(http.User);

        return Results.Ok(new OnboardingStatusResponse(
            hasAdministrator,
            authenticated,
            linked,
            settings.OnboardingComplete,
            NextStep(hasAdministrator, vrchat, connection, linked, group, settings.OnboardingComplete),
            vrchat,
            connection,
            group,
            integrations));
    }

    /// <summary>
    /// The first step that is not done yet, in spec 7.1's order.
    /// </summary>
    /// <remarks>
    /// The connection check is treated as satisfied by a successful VRChat verification that has
    /// not been re-checked, because verification <em>is</em> a live round trip to the API — an
    /// account cannot be verified over a network that cannot reach VRChat. The step still exists
    /// and is still re-runnable; it simply does not block an operator whose egress already
    /// demonstrably works (spec 7.1.1's "Pass → no proxy needed. Continue.").
    /// </remarks>
    private static OnboardingStep NextStep(
        bool hasAdministrator,
        VRChatAccountStatus vrchat,
        ConnectionStatus connection,
        bool linked,
        ManagedGroupStatus? group,
        bool complete)
    {
        if (!hasAdministrator)
            return OnboardingStep.Administrator;

        if (vrchat.VerifiedAt is null)
            return OnboardingStep.VRChat;

        if (connection.CheckedAt is null)
            return OnboardingStep.Connection;

        // The administrator's own link (design §4.3). Only reachable once the gate works, which
        // the two steps above establish.
        if (!linked)
            return OnboardingStep.LinkVRChat;

        if (group is null)
            return OnboardingStep.Group;

        return complete ? OnboardingStep.Done : OnboardingStep.Optional;
    }
}
