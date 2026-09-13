using System.Text.RegularExpressions;

namespace Modbot.VRChat;

/// <summary>
/// Tells a Cloudflare block apart from a VRChat error.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters because the remedies have nothing in common. A 403 from VRChat means
/// the account lacks a permission; a 403 from Cloudflare means this host's IP range cannot reach
/// the API at all, which is common on datacentre and VPS hosting, is not the operator's fault,
/// and is fixed by configuring one egress proxy (spec 2.3.1, 7.1.1). Surfacing the second as
/// "permission denied" sends an operator to check their group roles for an afternoon.
/// </para>
/// <para>
/// The body has to be parsed out of text rather than read from a header. On the SDK's error path
/// the response is rebuilt with an empty header collection, so there is nothing structured left —
/// and VRChat sends no <c>Retry-After</c> either, so no header worth recovering is being missed
/// (spec 4.1). What survives is the message, which carries the body inside
/// <c>"Error calling {method}: {body}"</c>.
/// </para>
/// </remarks>
public static class WafBlock
{
    /// <summary>
    /// Phrases that only appear on a Cloudflare interstitial. Matching on any one of them alone
    /// would be fragile; matching on any of several is how these pages are actually recognised,
    /// and the alternative — treating every 403 as a WAF block — would tell operators to buy a
    /// proxy to fix a permissions problem.
    /// </summary>
    private static readonly string[] Markers =
    [
        "cloudflare",
        "cf-ray",
        "attention required",
        "you have been blocked",
        "__cf_",
        "cf_chl_",
    ];

    /// <summary>Cloudflare's own error code, as it appears on the block page.</summary>
    private static readonly Regex CodePattern = new(
        @"(?:error\s*code|""code""\s*:|\berror\b)\s*[:\s]?\s*(1\d{3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Classifies a response. <paramref name="wafCode"/> is Cloudflare's code where the page
    /// carries one, and <c>0</c> where it is recognisably Cloudflare but unnumbered.
    /// </summary>
    /// <remarks>
    /// A 429 is never classified as a WAF block even when Cloudflare issued it. The response to a
    /// rate limit is the cold stop (spec 4.3.1), and routing one to "configure a proxy" would
    /// suggest changing IP in response to a limit — the behaviour the limits exist to prevent and
    /// the thing spec 2.3.1 exists to refuse.
    /// </remarks>
    public static bool TryClassify(int statusCode, string? errorText, string? rawContent, out int? wafCode)
    {
        wafCode = null;

        if (statusCode == 429)
            return false;

        var body = Payload(rawContent) ?? Payload(errorText);
        if (string.IsNullOrWhiteSpace(body))
            return false;

        if (!Markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;

        var match = CodePattern.Match(body);
        wafCode = match.Success ? int.Parse(match.Groups[1].ValueSpan, provider: null) : 0;

        return true;
    }

    /// <summary>
    /// Recovers the response body from the SDK's <c>"Error calling {method}: {body}"</c> wrapper,
    /// leaving anything that is already a body alone.
    /// </summary>
    public static string? Payload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.AsSpan().IndexOfAny('{', '<');

        return start > 0 ? text[start..] : text;
    }
}
