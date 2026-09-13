namespace Modbot.Core.Configuration;

/// <summary>
/// The address people use to reach this Modbot, checked and tidied once, here.
/// </summary>
/// <remarks>
/// Links sent out of band -- a reset link by email or Discord -- are built from this and from
/// nothing else (accounts and access design §4.2). It is typed by a person and confirmed by a
/// person. The one thing the server contributes is a suggestion: on Railway, the domain Railway
/// says it gave the service.
/// </remarks>
public static class PublicAddress
{
    /// <summary>The variable Railway sets to the service's public hostname. Read once, at boot, to suggest.</summary>
    public const string RailwayDomainVariable = "RAILWAY_PUBLIC_DOMAIN";

    /// <summary>
    /// An absolute http(s) origin with no path, query or trailing slash, or an error sentence.
    /// </summary>
    public static (string? Address, string? Error) Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (null, null);

        var text = input.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            return (null, "The public address must be a web address such as https://modbot.example.com.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            return (null, "The public address is just the start of the address: no path after the host.");

        return (uri.GetLeftPart(UriPartial.Authority), null);
    }

    /// <summary>The suggestion the platform can make, or null when it cannot.</summary>
    public static string? Suggest(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        var domain = read(RailwayDomainVariable);
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var (address, _) = Normalize(domain);
        return address;
    }
}
