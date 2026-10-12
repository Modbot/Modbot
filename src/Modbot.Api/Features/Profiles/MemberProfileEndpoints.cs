using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;

namespace Modbot.Api.Features.Profiles;

/// <summary>One answered profile field of one member.</summary>
/// <param name="Answer">The option's label or the member's own words.</param>
/// <param name="Visibility">only-me, staff or members: who the member lets read it.</param>
public sealed record MemberProfileLineView(Guid FieldId, string Name, string Kind, string Answer, string Visibility);

public sealed record MemberProfileView(IReadOnlyList<MemberProfileLineView> Lines);

/// <summary>
/// A member's profile as staff read it in Modbot (member profiles design §5): every answered
/// field, the ones the member kept to themselves included.
/// </summary>
public static class MemberProfileEndpoints
{
    public static IEndpointRouteBuilder MapMemberProfiles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/discord/members/{id}/profile", async (
                [FromRoute] string id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var lines = await MemberProfiles.LinesAsync(db, id, ProfileViewer.Staff, ct);

                return Results.Ok(new MemberProfileView([.. lines.Select(l => new MemberProfileLineView(
                    l.FieldId,
                    l.Name,
                    l.Kind == ProfileFieldKind.Text ? ProfileFieldEndpoints.TextName : ProfileFieldEndpoints.PickListName,
                    l.Answer ?? string.Empty,
                    ProfileFieldEndpoints.Visibility(l.Visibility)))]));
            })
            .WithTags("Discord")
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordMemberProfile")
            .WithSummary("Get Discord member profile")
            .WithDescription(
                "The profile fields one Discord member has filled in, with who the member lets read each. "
                + "Staff see every answer, including the ones kept to the member alone.")
            .Produces<MemberProfileView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
