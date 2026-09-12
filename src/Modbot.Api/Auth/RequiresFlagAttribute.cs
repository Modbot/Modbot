using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Auth;

/// <summary>
/// Requires the caller to hold every listed permission.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec 7.3, carried over from the old implementation, which had the right shape.
/// <c>RequiresGroupFlagAttribute</c> is deliberately not carried over: it existed for
/// multi-tenancy, and Modbot is a single-group appliance (§2.4).
/// </para>
/// <para>
/// It derives from <see cref="AuthorizeAttribute"/> and implements
/// <see cref="IAuthorizationRequirementData"/>, so the flags travel as endpoint metadata and no
/// named policy has to be registered per permission combination -- there are 2^n of those and a
/// registry of them is a thing to forget to update.
/// </para>
/// <para>
/// Requiring <em>all</em> listed flags rather than any is deliberate: "any" quietly widens access
/// every time a flag is added to an existing attribute, and widening should never be the accident.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresFlagAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public RequiresFlagAttribute(ModbotPermissions flags) => Flags = flags;

    public ModbotPermissions Flags { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
        => [new PermissionRequirement(Flags)];
}

/// <summary>Endpoint-builder sugar for the minimal APIs the feature slices map (spec 2.8).</summary>
public static class RequiresFlagExtensions
{
    public static TBuilder RequiresFlag<TBuilder>(this TBuilder builder, ModbotPermissions flags)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new RequiresFlagAttribute(flags));
        return builder;
    }
}

internal sealed class PermissionRequirement(ModbotPermissions flags) : IAuthorizationRequirement
{
    public ModbotPermissions Flags { get; } = flags;
}

internal sealed class PermissionAuthorizationHandler
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var held = ModbotAuth.PermissionsOf(context.User);

        // Administrator is checked rather than expanded, so a permission introduced after this
        // account was created is covered without a data migration.
        if (held.HasFlag(ModbotPermissions.Administrator)
            || (held & requirement.Flags) == requirement.Flags)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
