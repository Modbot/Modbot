using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Modbot.Landing.Features.Pages;

/// <summary>
/// Where my.modbot.co is, from <c>MODBOT_MY_URL</c>.
/// </summary>
/// <remarks>
/// The pages are prerendered at build time with the project's own address in them, so pointing them
/// somewhere else is a substitution as each page is first read. A group that runs its own selector
/// sets one variable and every link on the page follows.
/// </remarks>
public sealed record MyModbotAddress(string Url);

/// <summary>
/// The HTML files <c>Web/</c> builds into <c>wwwroot</c>: the landing page, the rooms page, the
/// not-found page, and the privacy policy when <c>PRIVACY_POLICY.md</c> existed at build time.
/// </summary>
public sealed partial class BuiltPages(IWebHostEnvironment environment, MyModbotAddress myModbot)
{
    public const string LandingFile = "index.html";
    public const string NotFoundFile = "404.html";
    public const string PrivacyFile = "privacy.html";
    public const string RoomsFile = "rooms.html";

    public static readonly string[] All = [LandingFile, NotFoundFile, PrivacyFile, RoomsFile];

    private readonly Dictionary<string, BuiltPage> _pages = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// The page, or null when it has not been built. Read once found; not remembered while missing,
    /// so a build that lands after start-up is picked up.
    /// </summary>
    public BuiltPage? Find(string file)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(file, out var known))
                return known;

            var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var path = Path.Combine(root, file);

            if (!File.Exists(path))
                return null;

            var html = File.ReadAllText(path);

            // Only the exact default address, and only when it is not the one being served. The
            // inline theme script carries no link, so the page's own script hash is unaffected.
            if (!string.Equals(myModbot.Url, Configuration.LandingEnvironment.DefaultMyUrl, StringComparison.Ordinal))
                html = html.Replace(Configuration.LandingEnvironment.DefaultMyUrl, myModbot.Url, StringComparison.Ordinal);

            // The rooms page shows worlds and group icons, which are pictures on VRChat's own
            // servers. Which host names those are is VRChat's to change, and a page that quietly
            // stopped showing pictures because a CDN moved is a fault nobody would find -- so
            // pictures over https are allowed from anywhere, on this one page. Everything else is
            // still 'self', and every address the page is given has already been checked to be an
            // https URL twice: by the Modbot that reported it and by the Cloud that stored it.
            var page = BuiltPage.From(html, file == RoomsFile ? "https:" : null);
            _pages[file] = page;
            return page;
        }
    }

    [GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>(?<body>[\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    internal static partial Regex InlineScript();
}

/// <param name="Html">The file as built.</param>
/// <param name="ContentSecurityPolicy">
/// A policy that allows this page's own inline scripts by hash and nothing else inline. The landing
/// page has one: the few lines that set the theme before first paint, so a dark-mode visitor never
/// sees a white flash. Hashing it here, from the file actually served, means a change to that script
/// can never leave the policy out of step with it.
/// </param>
public sealed record BuiltPage(string Html, string ContentSecurityPolicy)
{
    /// <param name="html">The built page.</param>
    /// <param name="extraImageSources">Extra <c>img-src</c> entries, or null for none.</param>
    public static BuiltPage From(string html, string? extraImageSources = null)
    {
        var hashes = BuiltPages.InlineScript()
            .Matches(html)
            .Select(m => m.Groups["body"].Value)
            .Where(body => body.Length > 0)
            .Select(body => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}'")
            .Distinct();

        var policy = string.Join("; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', ["'self'", .. hashes])}",
            // React writes style attributes into the prerendered markup.
            "style-src 'self' 'unsafe-inline'",
            $"img-src 'self' data:{(extraImageSources is null ? "" : $" {extraImageSources}")}",
            // Vite inlines the smallest font subsets into the stylesheet as data: URLs.
            "font-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");

        return new BuiltPage(html, policy);
    }
}
