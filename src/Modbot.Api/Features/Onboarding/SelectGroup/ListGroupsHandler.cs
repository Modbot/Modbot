using Microsoft.AspNetCore.Http;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;
using VRChat.API.Model;

namespace Modbot.Api.Features.Onboarding.SelectGroup;

/// <summary>
/// Spec 7.1 step 4: the groups this VRChat account can actually moderate.
/// </summary>
/// <remarks>
/// <para>
/// Two calls, not one per group. VRChat's <c>/users/{id}/groups</c> returns names and member
/// counts but no permissions, and <c>/groups/{id}</c> returns permissions one group at a time —
/// which at the <c>groups.read</c> pacing of one request per five seconds would mean a wizard
/// step that takes minutes for an account in twenty groups. <c>/users/{id}/groups/permissions</c>
/// answers the whole question in one request, so the step costs two.
/// </para>
/// <para>
/// Both are classified <c>users.read</c>, which is what they are: user-scoped reads on the
/// authenticated account. Spec 4.3.4's standing instruction applies — the real limit on
/// <c>/users/{id}/groups/permissions</c> has not been measured, and it is budgeted alongside its
/// nearest neighbour rather than given a rate of its own on a guess.
/// </para>
/// </remarks>
public static class ListGroupsHandler
{
    public static async Task<IResult> HandleAsync(
        IVRChatGate gate,
        IMonotonicClock elapsed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(elapsed);

        var started = elapsed.Elapsed;
        var session = await gate.SignInAsync(ct);

        if (!session.Success || session.Value?.Id is not { Length: > 0 } userId)
        {
            var took = (long)Math.Max(0, (elapsed.Elapsed - started).TotalMilliseconds);
            return Results.UnprocessableEntity(
                ConnectionDiagnosis.Describe(session, session.Value?.DisplayName, took));
        }

        var groups = await gate.ExecuteAsync<List<LimitedUserGroups>>(
            new VRChatEndpoint(VRChatEndpointClass.UsersRead, userId, "GetUserGroups"),
            (vrchat, token) => vrchat.Users.GetUserGroupsWithHttpInfoAsync(userId, token),
            // A human is watching a spinner in a setup wizard. Background sync waits.
            VRChatCallPriority.Interactive,
            ct);

        if (!groups.Success)
            return Failed(groups, elapsed.Elapsed - started);

        var permissions = await gate.ExecuteAsync<Dictionary<string, List<GroupPermissions>>>(
            new VRChatEndpoint(VRChatEndpointClass.UsersRead, userId, "GetUserAllGroupPermissions"),
            (vrchat, token) => vrchat.Users.GetUserAllGroupPermissionsWithHttpInfoAsync(
                userId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);

        if (!permissions.Success)
            return Failed(permissions, elapsed.Elapsed - started);

        var all = groups.Value ?? [];
        var held = permissions.Value ?? [];

        var candidates = all
            .Where(group => group.GroupId is { Length: > 0 })
            .Select(group => new
            {
                Group = group,
                Held = held.TryGetValue(group.GroupId, out var p) ? p : [],
            })
            .Where(entry => ModeratorPermissions.Qualifies(entry.Held))
            .Select(entry => new GroupCandidate(
                entry.Group.GroupId,
                entry.Group.Name ?? entry.Group.GroupId,
                entry.Group.MemberCount,
                entry.Group.IconUrl,
                entry.Group.ShortCode,
                [.. entry.Held.Select(ModeratorPermissions.Name)],
                ModeratorPermissions.Missing(entry.Held)))
            // Largest first: the group somebody is standing up a moderation appliance for is
            // almost never the three-person one they made to test something.
            .OrderByDescending(candidate => candidate.MemberCount)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new GroupCandidatesResponse(
            candidates, all.Count, ModeratorPermissions.RequiredNames));
    }

    private static IResult Failed<T>(VRChatResult<T> result, TimeSpan took) =>
        Results.UnprocessableEntity(ConnectionDiagnosis.Describe(
            result, null, (long)Math.Max(0, took.TotalMilliseconds)));
}
