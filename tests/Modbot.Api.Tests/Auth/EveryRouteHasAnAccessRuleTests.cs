using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Tests.Auth;

/// <summary>
/// A route nobody thought to protect is the failure this stops.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in ASP.NET Core says "this route has no access rule": a route mapped with neither
/// <c>RequiresFlag</c>, <c>RequireAuthorization</c> nor <c>AllowAnonymous</c> simply answers
/// everybody. That is exactly what a new endpoint is when its author forgets, and a permission
/// dropped from an existing one looks the same. Every route now says one or the other on
/// purpose: an access rule, or a deliberate <c>AllowAnonymous</c> for the few that are public
/// (sign-in, the server's public page, the setup wizard, the link pages).
/// </para>
/// <para>
/// No database and no Docker: the routes are read from the framework's own table after mapping,
/// so the check covers what is shipped and cannot drift from a list kept by hand.
/// </para>
/// </remarks>
public class EveryRouteHasAnAccessRuleTests
{
    [Fact]
    public void EveryRouteChecksWhoIsAskingOrIsDeliberatelyOpen()
    {
        var routes = ApiTestHost.MappedRoutes();

        // A walk that found nothing would pass for the wrong reason.
        Assert.True(routes.Count > 100, $"Only {routes.Count} routes were found, so the walk is not seeing the API.");

        var unguarded = routes
            .Where(route => route.Metadata.GetMetadata<IAllowAnonymous>() is null
                            && route.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These routes have no access rule. Add RequiresFlag (or RequireAuthorization) if a signed-in person "
            + "may use them, or AllowAnonymous if anybody may:"
            + Environment.NewLine + string.Join(Environment.NewLine, unguarded));
    }

    private static string Describe(RouteEndpoint route)
    {
        var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join(",", methods)} {route.RoutePattern.RawText}";
    }
}
