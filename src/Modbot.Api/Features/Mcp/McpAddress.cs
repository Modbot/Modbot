using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Mcp;

/// <summary>Where this Modbot's MCP server is, as the addresses an AI app is told.</summary>
/// <param name="Origin">The start of every address: <c>https://modbot.example.com</c>.</param>
public sealed record McpAddress(string Origin)
{
    public const string ServerPath = "/mcp";

    public string ServerUrl => Origin + ServerPath;

    public string AuthorizeUrl => Origin + "/mcp/authorize";

    public string TokenUrl => Origin + "/mcp/token";

    public string RegisterUrl => Origin + "/mcp/register";

    public string RevokeUrl => Origin + "/mcp/revoke";

    /// <summary>The protected resource metadata for <c>/mcp</c> (RFC 9728, path-suffixed).</summary>
    public string ResourceMetadataUrl => Origin + "/.well-known/oauth-protected-resource" + ServerPath;

    /// <summary>
    /// The saved public address when there is one, otherwise the address this request came to.
    /// </summary>
    /// <remarks>
    /// The public address rule (accounts and access design §4.2) is about links delivered to
    /// third parties, where a request's host would let a stranger choose where a victim's link
    /// points. Here the answer goes back to whoever asked, about the address they asked at, so
    /// the request's own origin is a fine fallback -- and it is what makes a Modbot on
    /// <c>http://localhost</c> work before anyone has saved an address.
    /// </remarks>
    public static async Task<McpAddress> ForAsync(HttpContext http, ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);

        var saved = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct);

        return new McpAddress(saved ?? $"{http.Request.Scheme}://{http.Request.Host}");
    }
}
