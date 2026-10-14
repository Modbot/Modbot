namespace Modbot.Core.Data.Entities;

/// <summary>
/// The sites people pay supporter money through. One word per site, so a second site (Patreon) uses
/// the same tables and the same screen as the first.
/// </summary>
public static class SupporterProviders
{
    public const string Kofi = "kofi";

    public static bool IsKnown(string? provider) => provider is Kofi;
}

/// <summary>Whether a supporter's roles are on or off.</summary>
public static class SupporterStates
{
    /// <summary>Paid up: the tier's roles are given.</summary>
    public const string Active = "active";

    /// <summary>The next payment did not come in time: the roles Modbot gave are taken away.</summary>
    public const string Ended = "ended";
}

/// <summary>What supporters are limited to, in one place for the intake, the page and the pass.</summary>
public static class SupporterLimits
{
    /// <summary>The longest a tier's name may be.</summary>
    public const int MostTierNameLength = 200;

    /// <summary>The longest the thank-you message may be. Discord's own limit for a message is 2000.</summary>
    public const int MostThanksLength = 1500;

    /// <summary>The longest the verification token may be.</summary>
    public const int MostTokenLength = 200;

    /// <summary>The most tiers kept.</summary>
    public const int MostTiers = 50;

    /// <summary>How many supporters the page lists, newest payment first.</summary>
    public const int MostListed = 500;

    /// <summary>How many days after the paid month is up a supporter keeps the roles before they are taken away.</summary>
    public const int GraceDays = 3;
}

/// <summary>
/// One level a supporter can pay at, with the Discord role and the VRChat group role it gives.
/// The table is <c>supporter_tier</c>.
/// </summary>
/// <remarks>
/// A tier is made the first time a payment names it (with no roles yet) or by staff. Which site it
/// belongs to is <see cref="Provider"/>; <see cref="Key"/> is how that site names it (Ko-fi: the
/// tier's name), so Patreon can add its own rows without a second mapping.
/// </remarks>
public class SupporterTier
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>One of <see cref="SupporterProviders"/>.</summary>
    public string Provider { get; set; } = SupporterProviders.Kofi;

    /// <summary>How the site names the tier, trimmed and lower-case, so "Gold" and "gold" are one tier.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The tier's name as the site wrote it, for the page and the thank-you message.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The Discord role this tier gives. Opaque text; null for none.</summary>
    public string? DiscordRoleId { get; set; }

    /// <summary>The VRChat group role this tier gives. Opaque text, never validated; null for none.</summary>
    public string? VRChatRoleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A person who pays monthly through one of the sites. The table is <c>supporter</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DiscordUserId"/> is how a supporter is matched to the server: Ko-fi's Discord link, or
/// staff linking them by the code from their payment. Without it the supporter waits on the page.
/// </para>
/// <para>
/// The <c>Given…</c> columns are what Modbot itself gave. Roles are only ever taken away when they
/// are recorded here, so a role somebody gave by hand is never touched.
/// </para>
/// </remarks>
public class Supporter
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>One of <see cref="SupporterProviders"/>.</summary>
    public string Provider { get; set; } = SupporterProviders.Kofi;

    /// <summary>
    /// Tells payments from the same person apart: a fingerprint of their email address (the address
    /// itself is never kept), or their Discord account when Ko-fi sent no email.
    /// </summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>The name they paid under.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The Discord account this supporter is, or null while they have not been matched.</summary>
    public string? DiscordUserId { get; set; }

    /// <summary>The tier of their latest payment.</summary>
    public Guid? TierId { get; set; }

    /// <summary>One of <see cref="SupporterStates"/>.</summary>
    public string State { get; set; } = SupporterStates.Active;

    public DateTimeOffset FirstPaidAt { get; set; }

    public DateTimeOffset LastPaidAt { get; set; }

    /// <summary>The month the last payment covers is over at this moment.</summary>
    public DateTimeOffset PaidThrough { get; set; }

    /// <summary>When the thank-you message was tried. Null until it has been; cleared again for a new tier or a comeback.</summary>
    public DateTimeOffset? ThanksSentAt { get; set; }

    /// <summary>The Discord role Modbot gave and has not taken back.</summary>
    public string? GivenDiscordRoleId { get; set; }

    /// <summary>The VRChat group role Modbot gave and has not taken back.</summary>
    public string? GivenVRChatRoleId { get; set; }

    /// <summary>The VRChat account that role was given to (the account linked to their Discord at the time).</summary>
    public string? GivenVRChatUserId { get; set; }

    /// <summary>The last role change that was refused, as a sentence. Null when the last one went through.</summary>
    public string? Problem { get; set; }

    /// <summary>Roles are left alone until this moment after a refusal.</summary>
    public DateTimeOffset? TryAgainAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One payment Modbot has taken in, so a repeat of the same message is counted once and staff can
/// find a supporter by the code on their receipt. The table is <c>supporter_payment</c>.
/// </summary>
public class SupporterPayment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Provider { get; set; } = SupporterProviders.Kofi;

    /// <summary>The site's own code for the payment (Ko-fi: the transaction id on the receipt).</summary>
    public string Code { get; set; } = string.Empty;

    public Guid SupporterId { get; set; }

    public DateTimeOffset PaidAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
