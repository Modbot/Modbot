using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Core;
using Modbot.Core.Data;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Server;

/// <summary>
/// What this Modbot is, for anyone who asks.
/// </summary>
/// <param name="Name">The group this server manages, as VRChat last named it.</param>
/// <param name="GroupId">That group's id. Opaque; never validated (spec 3.1.1).</param>
/// <param name="IconUrl">The group's icon, as VRChat last showed it.</param>
/// <param name="BannerUrl">The group's banner.</param>
/// <param name="OwnerEmail">
/// The address of the person who runs this server (<see cref="OwnerAccount"/>), or null when the
/// operator has turned that off or no enabled administrator has an address.
/// </param>
/// <param name="Version">The Modbot release this server is running.</param>
/// <param name="PublicAddress">The address the operator saved as this server's own, or null.</param>
public sealed record ServerInfo(
    string? Name,
    string? GroupId,
    string? IconUrl,
    string? BannerUrl,
    string? OwnerEmail,
    string? Version,
    string? PublicAddress);

/// <summary>
/// <c>GET /api/server</c>: this deployment described to a stranger's browser.
/// </summary>
/// <remarks>
/// <para>
/// <strong>"Server", not "instance."</strong> In Modbot an instance is a VRChat instance and
/// nothing else -- <c>/api/instances</c> already means that, and the naming rule is one word for
/// one thing. The thing this describes is the Modbot server a group runs.
/// </para>
/// <para>
/// <strong>Anonymous, and 200 before setup is finished.</strong> my.modbot.co reads it from the
/// browser of somebody who is adding a server, who has no account on it and may never have one.
/// Every field is null until the answer is known, and a fresh deployment answers 200 with nulls
/// rather than 404: "this is a Modbot that has not been set up yet" is a useful answer, and a 404
/// is indistinguishable from a wrong address.
/// </para>
/// <para>
/// <strong>What it does not say.</strong> Nothing about members, moderation, staff accounts,
/// instances or settings, and nothing that is a secret. The group, the version and the address are
/// already visible to anyone who opens the sign-in page; the owner's email is the one field added
/// on purpose, and the one an operator can withhold (<c>server.showOwnerEmail</c>).
/// </para>
/// <para>
/// <strong>CORS.</strong> The <c>Access-Control-Allow-Origin</c> header is written here rather than
/// through the CORS middleware, because that would mean adding middleware to every host that maps
/// this API for the sake of one endpoint. A plain cross-origin <c>GET</c> with no custom headers
/// is a simple request and needs no preflight; the <c>OPTIONS</c> answer below is there for a
/// client that sends one anyway.
/// </para>
/// </remarks>
public static class ServerEndpoint
{
    public static IEndpointRouteBuilder MapServerInfo(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/server", async (
                [FromServices] ModbotContext db,
                HttpContext http,
                CancellationToken ct) =>
            {
                AllowAnyOrigin(http);

                var settings = await db.GetSettingsAsync(ct);

                var ownerEmail = settings.ServerShowOwnerEmail
                    ? await OwnerAccount.EmailAsync(db, ct)
                    : null;

                return Results.Ok(new ServerInfo(
                    Blank(settings.ManagedGroupName),
                    Blank(settings.ManagedGroupId),
                    Blank(settings.ManagedGroupIconUrl),
                    Blank(settings.ManagedGroupBannerUrl),
                    ownerEmail,
                    ModbotVersion.Release,
                    Blank(settings.PublicAddress)));
            })
            .WithTags("Server")
            .WithName("GetServer")
            .WithSummary("What this Modbot is: its group, its version and who runs it")
            .WithDescription(
                "Unauthenticated, readable from any origin, and answers 200 with nulls on a "
                + "deployment that has not been set up yet.\n\n"
                + "ownerEmail is the address of the oldest enabled account holding Administrator "
                + "that has one. It is null when the operator has turned off "
                + "`server.showOwnerEmail`.")
            .Produces<ServerInfo>()
            .AllowAnonymous();

        // For a client that sends a preflight -- a fetch with a header of its own, say. Left out
        // of the reference: it describes no resource.
        app.MapMethods("/api/server", [HttpMethods.Options], (HttpContext http) =>
            {
                AllowAnyOrigin(http);
                http.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
                http.Response.Headers.AccessControlAllowHeaders = "*";
                http.Response.Headers.AccessControlMaxAge = "86400";

                return Results.NoContent();
            })
            .ExcludeFromDescription()
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Any origin, because the page that reads this belongs to whoever is adding the server and
    /// the answer carries nothing a stranger could not read off the sign-in page. No credentials
    /// are allowed with it, so a session cookie can never ride along.
    /// </summary>
    private static void AllowAnyOrigin(HttpContext http)
        => http.Response.Headers.AccessControlAllowOrigin = "*";

    /// <summary>Empty is not an answer; null is.</summary>
    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
