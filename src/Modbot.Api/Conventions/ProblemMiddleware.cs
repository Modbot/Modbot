using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Conventions;

/// <summary>
/// Gives an error that left the pipeline with no body the problem shape: a parameter that could not
/// be read, a 401 from sign-in, a 403 from a <c>Forbid()</c>.
/// </summary>
/// <remarks>
/// The same test the framework's status code pages make -- the response has not started, and
/// nothing has said what it holds -- so an endpoint that wrote anything of its own, even an empty
/// JSON object, is never written over. Only under <see cref="Problems.Covers"/>, and never on the
/// VRChat proxy, where an empty answer is VRChat's own.
/// </remarks>
public static class ProblemMiddleware
{
    public static IApplicationBuilder UseApiProblems(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            await next(context);

            var response = context.Response;

            if (response.HasStarted
                || response.StatusCode < 400
                || response.ContentLength is not null
                || !string.IsNullOrEmpty(response.ContentType)
                || !Problems.Covers(context.Request.Path)
                || Problems.IsPassedOn(context.Request.Path))
            {
                return;
            }

            await Problems.WriteAsync(context, response.StatusCode, null);
        });
    }
}

/// <summary>
/// Says which permission a refused request needed, and when it was the missing VRChat link rather
/// than a permission, says that instead.
/// </summary>
/// <remarks>
/// The default handler runs first and does what it always did (a challenge, a forbid); this only
/// adds the body, so sign-in itself is unchanged. <c>neededPermissions</c> names every permission
/// the endpoint asks for, the same names the roles page and <c>/api/auth/me</c> use.
/// </remarks>
internal sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        await _default.HandleAsync(next, context, policy, authorizeResult);

        if (authorizeResult.Succeeded
            || context.Response.HasStarted
            || !string.IsNullOrEmpty(context.Response.ContentType)
            || !Problems.Covers(context.Request.Path))
        {
            return;
        }

        if (authorizeResult.Challenged)
        {
            await Problems.WriteAsync(
                context, StatusCodes.Status401Unauthorized, "Not signed in, or the API key is not valid.", Problems.NotSignedIn);
            return;
        }

        if (!authorizeResult.Forbidden)
            return;

        var failed = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList() ?? [];

        if (failed.OfType<VRChatLinkedRequirement>().Any())
        {
            await Problems.WriteAsync(
                context, StatusCodes.Status403Forbidden, "Link your VRChat account first.", Problems.VRChatNotLinked);
            return;
        }

        var needed = failed.OfType<PermissionRequirement>()
            .Aggregate(ModbotPermissions.None, (all, r) => all | r.Flags);

        var names = PermissionCatalog.NamesOf(needed);

        await Problems.WriteAsync(
            context,
            StatusCodes.Status403Forbidden,
            names.Count == 0
                ? "You do not have permission to do that."
                : $"This needs the {string.Join(" and ", names)} permission{(names.Count > 1 ? "s" : "")}.",
            Problems.NeedsPermission,
            names.Count == 0 ? null : new JsonObject { ["neededPermissions"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]) });
    }
}
