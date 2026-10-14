using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Supporters;

/// <summary>
/// The few rules about when a supporter keeps their roles, and how one is told from another. Plain
/// functions so the page, the intake and the pass all say the same thing.
/// </summary>
public static class SupporterRules
{
    /// <summary>The moment the month a payment covers is over.</summary>
    public static DateTimeOffset PaidThrough(DateTimeOffset paidAt) => paidAt.AddMonths(1);

    /// <summary>
    /// The moment the roles come off if no new payment has come: three days after the paid month is
    /// up. Ko-fi sends no message when a membership stops, so a missing payment is the only sign.
    /// </summary>
    public static DateTimeOffset RolesEndAt(DateTimeOffset paidThrough) => paidThrough.AddDays(SupporterLimits.GraceDays);

    /// <summary>Whether a supporter whose month ran out at <paramref name="paidThrough"/> has lost the roles by <paramref name="now"/>.</summary>
    public static bool HasLapsed(DateTimeOffset paidThrough, DateTimeOffset now) => now >= RolesEndAt(paidThrough);

    /// <summary>A tier's key: how the site names it, trimmed and lower-case.</summary>
    public static string TierKeyOf(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// Tells one payer from another without keeping their email address: a fingerprint of it. With no
    /// email the Discord account stands in, and with neither the name does.
    /// </summary>
    public static string PayerIdOf(string provider, string? email, string? discordUserId, string? name)
    {
        var basis = !string.IsNullOrWhiteSpace(email)
            ? "email:" + email.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(discordUserId)
                ? "discord:" + discordUserId.Trim()
                : "name:" + (name ?? string.Empty).Trim().ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(provider + "|" + basis));
        return Convert.ToHexStringLower(hash)[..40];
    }

    /// <summary>Whether two secrets are the same, in a time that does not depend on how much of them matches.</summary>
    public static bool SameSecret(string? given, string? stored)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(stored))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(stored)));
    }

    /// <summary>The thank-you message with {name} and {tier} filled in.</summary>
    public static string Thanks(string template, string name, string tier)
        => template.Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{tier}", tier, StringComparison.OrdinalIgnoreCase);
}
