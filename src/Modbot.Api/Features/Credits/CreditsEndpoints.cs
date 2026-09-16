using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Credits;

/// <summary>
/// The people the project wants to thank, for the Credits page.
/// </summary>
/// <remarks>
/// Any signed-in account, like the rest of Credits: nothing here is about the group, and it is the
/// same list Modbot Cloud publishes to everyone. Read through Modbot rather than from the browser
/// so that a deployment which has turned Cloud off makes no request to it at all, and so one
/// deployment asks Cloud a few times a day rather than once per moderator per visit.
/// </remarks>
public static class CreditsEndpoints
{
    public static IEndpointRouteBuilder MapCredits(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/credits").WithTags("Credits").RequireAuthorization();

        group.MapGet("/showcase", async (
                // Optional: a host that mapped the API without registering it answers "nothing to
                // show" rather than failing to resolve a service mid-request.
                [FromServices] CloudShowcase? showcase,
                CancellationToken ct) =>
            {
                return Results.Ok(showcase is null
                    ? new Showcase(false, [], [], [])
                    : await showcase.ReadAsync(ct));
            })
            .WithName("GetCreditsShowcase")
            .WithSummary("Contributors, sponsors and early adopters, from Modbot Cloud")
            .WithDescription(
                "`available` is false when this Modbot has Modbot Cloud turned off, or could not "
                + "reach it. The Credits page shows nothing rather than an error: none of this "
                + "changes what Modbot does.")
            .Produces<Showcase>();

        return app;
    }
}
