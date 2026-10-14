using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Supporters;

/// <summary>What a membership message says about the patron's roles.</summary>
public enum PatreonStanding
{
    /// <summary>Nothing to act on: not a patron, or not one Modbot can tell.</summary>
    None,

    /// <summary>Paying: the tier's roles are on.</summary>
    Active,

    /// <summary>The last charge was declined: the roles stay a few days, then come off unless a later message says active.</summary>
    Declined,

    /// <summary>The pledge ended: the roles come off when the paid month is up.</summary>
    Ended,
}

/// <summary>The few rules about Patreon's messages: who signed them, where the patron stands, how they are told apart.</summary>
public static class PatreonRules
{
    /// <summary>The header Patreon puts the signature in.</summary>
    public const string SignatureHeader = "X-Patreon-Signature";

    /// <summary>The header Patreon names the trigger in, such as <c>members:pledge:create</c>.</summary>
    public const string EventHeader = "X-Patreon-Event";

    /// <summary>
    /// Whether <paramref name="signature"/> is the hex HMAC-MD5 of the message body made with the
    /// webhook secret, which is how Patreon signs. MD5 is Patreon's choice, not Modbot's; it is only
    /// used to check a message is Patreon's. The compare takes the same time wherever the two differ.
    /// </summary>
    public static bool IsSignedBy(ReadOnlySpan<byte> body, string? signature, string? secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(secret))
            return false;

        byte[] given;

        try
        {
            given = Convert.FromHexString(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body);

        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>
    /// Where the patron stands after this message. A message that says the pledge or the member was
    /// deleted ends it whatever the status field says.
    /// </summary>
    public static PatreonStanding StandingOf(string? trigger, string? patronStatus)
    {
        if (trigger?.Trim().ToLowerInvariant() is "members:pledge:delete" or "members:delete")
            return PatreonStanding.Ended;

        return patronStatus switch
        {
            "active_patron" => PatreonStanding.Active,
            "declined_patron" => PatreonStanding.Declined,
            "former_patron" => PatreonStanding.Ended,
            _ => PatreonStanding.None,
        };
    }

    /// <summary>
    /// A patron's fingerprint. Patreon's member id never changes and an email address can, so it is the
    /// member id that tells one patron from another; no email address is read.
    /// </summary>
    public static string PayerIdOf(string memberId)
        => SupporterRules.PayerIdOf(SupporterProviders.Patreon, null, null, "member:" + memberId);

    /// <summary>The code of a charge: the member and the moment of the charge.</summary>
    public static string PaymentCode(string memberId, DateTimeOffset charged)
        => memberId + "|" + charged.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
