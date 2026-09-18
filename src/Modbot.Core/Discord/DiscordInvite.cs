using System.Globalization;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>
/// The link that adds the bot to a Discord server, asking for the permissions the features that are
/// on need and no more (M5 §7, Discord account linking design §5).
/// </summary>
/// <remarks>
/// Built from the OAuth2 client id, which is the application id: one setting, not two. A feature
/// that needs another permission adds one line to <see cref="PermissionsFor"/>.
/// </remarks>
public static class DiscordInvite
{
    // Discord's permission bits.
    public const long RemoveMembers = 1L << 1;
    public const long BanMembers = 1L << 2;
    public const long ViewAuditLog = 1L << 7;
    public const long ViewChannel = 1L << 10;
    public const long SendMessages = 1L << 11;
    public const long EmbedLinks = 1L << 14;
    public const long ReadMessageHistory = 1L << 16;
    public const long ManageRoles = 1L << 28;

    /// <summary>The permissions the bot needs with these settings.</summary>
    public static long PermissionsFor(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Posting the moderation log, instance cards (fetched by id before each rewrite) and the
        // backup channel mention; and reading the audit log to catch up what the bot missed.
        var permissions = ViewChannel | SendMessages | EmbedLinks | ReadMessageHistory | ViewAuditLog;

        if (!string.IsNullOrWhiteSpace(settings.DiscordLinkedRoleId)
            || !string.IsNullOrWhiteSpace(settings.DiscordEighteenPlusRoleId)
            || settings.DiscordRoleSyncOn)
        {
            permissions |= ManageRoles;
        }

        // Only asked for when a ban may actually be copied into Discord (M5 §7). A deployment that
        // syncs roles and nothing else never hands the bot the ability to ban anybody.
        if (settings.DiscordBanSyncToDiscord)
        {
            permissions |= settings.DiscordBanCopyAction == DiscordBanCopyActions.Remove
                ? RemoveMembers
                : BanMembers;
        }

        return permissions;
    }

    /// <summary>The invite link, or null when no client id is set.</summary>
    public static string? LinkFor(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.DiscordOAuthClientId))
            return null;

        return "https://discord.com/oauth2/authorize"
            + "?client_id=" + Uri.EscapeDataString(settings.DiscordOAuthClientId.Trim())
            + "&scope=bot+applications.commands"
            + "&permissions=" + PermissionsFor(settings).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where Discord sends a member back after "Sign in with Discord". Built from the public address
    /// only (accounts and access design §4.2); null without one.
    /// </summary>
    public static string? RedirectUrlFor(string? publicAddress)
        => string.IsNullOrWhiteSpace(publicAddress)
            ? null
            : publicAddress.Trim().TrimEnd('/') + "/api/discord-link/callback";

    /// <summary>The member-facing link page, built from the public address only; null without one.</summary>
    public static string? LinkPageFor(string? publicAddress)
        => string.IsNullOrWhiteSpace(publicAddress)
            ? null
            : publicAddress.Trim().TrimEnd('/') + "/link";
}
