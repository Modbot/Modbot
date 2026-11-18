using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.ProblemAvatars;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.AvatarMarks;

/// <param name="VRChatUserId">The person whose current avatar is marked.</param>
public sealed record CompanionMarkAvatarRequest([property: JsonPropertyName("vrchatUserId")] string? VRChatUserId);

/// <summary>
/// Mark avatar on the person card: the avatar a person was last seen wearing goes on the problem
/// avatar list, as the owner of the paired companion that pressed it.
/// </summary>
/// <remarks>
/// <para><strong>The second place a device token speaks for a person</strong> (the first is Dictate,
/// <see cref="Chat.CompanionChatEndpoints"/>). The list is changed under the permission that manages
/// flag rules, so this asks for it of the device's owner at this request, from the same read of the
/// account every companion endpoint makes. A device whose owner does not hold it is refused with
/// 403, however it got here, and the person card never offers the button to one
/// (<see cref="ContextHandler.UserAsync"/> leaves the avatar out for them).</para>
/// <para><strong>Nothing else of the list is reachable.</strong> A device can add the avatar a
/// person was last seen in. It cannot add a name it typed, list the names, or remove one: those stay
/// on the signed-in routes. The entry records the owner as the one who added it, the same as the web
/// app does.</para>
/// <para>The adding itself is <see cref="ProblemAvatarEndpoints.AddAsync"/>, the web app's own code,
/// so a name already on the list comes back unchanged and the exact name VRChat's log gave is what
/// is stored.</para>
/// </remarks>
public static class CompanionProblemAvatarEndpoints
{
    public const string NotAllowed = "problem_avatar_not_allowed";

    public const string NoAvatar = "problem_avatar_unknown";

    public static async Task<IResult> MarkAsync(
        int apiVersion,
        [FromBody] CompanionMarkAvatarRequest? body,
        HttpContext context,
        [FromServices] DeviceAuthenticator authenticator,
        [FromServices] ModbotContext db,
        [FromServices] AccountFacts facts,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (!ModbotAuth.Allows(authentication.OwnerPermissions, ModbotPermissions.ManageSettings))
        {
            return Results.Json(
                new CompanionError(NotAllowed, "This account may not mark avatars."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (body?.VRChatUserId is not { Length: > 0 } person || string.IsNullOrWhiteSpace(person))
            return CompanionApiErrors.Malformed("Say which person.");

        if (await ProblemAvatarEndpoints.CurrentAvatarAsync(db, person, ct) is not { } name)
        {
            return Results.Json(
                new CompanionError(NoAvatar, "Modbot has not seen that person wear an avatar."),
                statusCode: StatusCodes.Status404NotFound);
        }

        var userId = authentication.Device!.IssuedToUserId;
        var username = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(await ProblemAvatarEndpoints.AddAsync(
            db, facts, clock, new Actor(userId, username ?? string.Empty), name, ct));
    }
}
