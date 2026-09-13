using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Onboarding;

/// <summary>
/// Who may run the setup wizard.
/// </summary>
/// <remarks>
/// <para>
/// Spec 7.1 states the rule in one sentence and it has two halves, both of which matter:
/// <strong>until a <see cref="ModbotUser"/> exists every route leads to the wizard, and once one
/// exists the wizard requires authentication.</strong> The first half is what makes a fresh
/// deployment usable at all — there is nobody to authenticate as. The second half is what stops
/// the wizard being a permanent unauthenticated door onto "point this deployment at a different
/// VRChat account and a different group".
/// </para>
/// <para>
/// It is a filter rather than an authorisation policy because the answer depends on a database
/// row, not on the caller's ticket, and because a policy returning "allow" for anonymous callers
/// on a fresh install and "deny" a second later is not something the authorisation system models
/// well. The endpoints are therefore mapped <c>AllowAnonymous</c> and this decides.
/// </para>
/// <para>
/// The permission required afterwards is <see cref="ModbotPermissions.ManageSettings"/>: every
/// wizard step writes to <c>Settings</c>, so anyone allowed to re-run one is, by definition,
/// changing settings. <see cref="ModbotPermissions.Administrator"/> satisfies it the same way it
/// does everywhere else (spec 7.3).
/// </para>
/// <para>
/// Steps after "link your VRChat account" also require that link (accounts and access design
/// §4.3). The steps before it cannot: the link is proved through the gate, and the gate is not
/// usable until the VRChat account step is done.
/// </para>
/// </remarks>
public sealed class OnboardingAccessFilter : IEndpointFilter
{
    private readonly bool _requireVRChatLink;

    public OnboardingAccessFilter(bool requireVRChatLink) => _requireVRChatLink = requireVRChatLink;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var accounts = http.RequestServices.GetRequiredService<UserAccountService>();

        if (!await accounts.AnyUsersAsync(http.RequestAborted).ConfigureAwait(false))
            return await next(context).ConfigureAwait(false);

        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        if (!ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageSettings))
            return Results.Forbid();

        if (_requireVRChatLink && !ModbotAuth.IsVRChatLinked(http.User))
            return Results.Forbid();

        return await next(context).ConfigureAwait(false);
    }
}

public static class OnboardingAccess
{
    /// <summary>
    /// The sentence every onboarding endpoint's OpenAPI description ends with, written once so
    /// the rule reads identically on all of them.
    /// </summary>
    public const string Rule =
        "Open while no staff account exists, because there is nobody to authenticate as yet. "
        + "From the moment one does, it requires a signed-in account holding ManageSettings — "
        + "otherwise the wizard stays an unauthenticated way to re-point a running deployment.";

    /// <param name="requireVRChatLink">
    /// True for the steps after "link your VRChat account". False for the steps before it, which
    /// an unlinked administrator has to be able to run to get the gate working.
    /// </param>
    public static TBuilder RequiresOnboardingAccess<TBuilder>(this TBuilder builder, bool requireVRChatLink = true)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(new OnboardingAccessFilter(requireVRChatLink));
        builder.AllowAnonymous();

        return builder;
    }
}
