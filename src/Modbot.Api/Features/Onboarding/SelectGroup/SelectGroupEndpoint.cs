using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Onboarding.SelectGroup;

public static class SelectGroupEndpoint
{
    public static IEndpointRouteBuilder MapSelectGroup(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/onboarding/groups", ListGroupsHandler.HandleAsync)
            .WithTags("Onboarding")
            .WithName("ListManageableGroups")
            .WithSummary("Groups where the VRChat account holds moderator permissions (spec 7.1, step 4)")
            .WithDescription(
                "Returns qualifying groups plus the total the account belongs to, so a short "
                + "list can say how many were filtered out and requiredPermissions can name what "
                + "they were missing. An empty list with a non-zero total is a permissions "
                + "problem with a specific fix, not an absence — and telling the two apart is "
                + "what spec 7.1 asks for here.\n\n"
                + "Each group also carries missingPermissions, so an operator can see before "
                + "choosing which Modbot features will not work until somebody grants one more.\n\n"
                + "Costs two VRChat requests regardless of how many groups the account is in.\n\n"
                + OnboardingAccess.Rule)
            .Produces<GroupCandidatesResponse>()
            .Produces<ConnectionDiagnosis>(StatusCodes.Status422UnprocessableEntity)
            .RequiresOnboardingAccess();

        app.MapPost("/api/onboarding/group", SelectGroupHandler.HandleAsync)
            .WithTags("Onboarding")
            .WithName("SelectManagedGroup")
            .WithSummary("Choose the group this deployment manages (spec 2.4, 7.1 step 4)")
            .WithDescription(
                "One deployment, one group. Re-runnable later from settings, though changing it "
                + "on a running deployment points every sync at different data.\n\n"
                + OnboardingAccess.Rule)
            .Produces<SelectGroupResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequiresOnboardingAccess();

        return app;
    }
}
