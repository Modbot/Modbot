using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.GroupPage;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Settings;

/// <param name="On">Whether the calendar's event form may upload a picture to VRChat.</param>
/// <param name="HasPlus">Whether Modbot's VRChat account has VRChat+; null until it has been read.</param>
public sealed record VRChatPictureUploadsView(
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("hasPlus")] bool? HasPlus = null);

public sealed record SetVRChatPictureUploadsRequest([property: JsonPropertyName("on")] bool On);

/// <summary>
/// Whether the calendar's event form may upload a picture to VRChat, on the VRChat account Modbot
/// signs in as (calendar design §2).
/// </summary>
/// <remarks>
/// Off by default, on every install: VRChat answers the upload with a refusal unless the account
/// Modbot signs in as has VRChat+. Off, <c>POST /api/calendar/vrchat-picture</c> answers "Picture
/// uploads are off." and VRChat is never asked; events keep the picture ids they already have, and
/// the form takes a <c>file_</c> id typed by hand, as it did before uploads existed. While Modbot's
/// account is known to have no VRChat+ the switch can be turned off but not on, and Modbot turns it
/// off itself (and records that) when it learns the account has none.
/// </remarks>
public static class VRChatPictureSettingsEndpoints
{
    public static IEndpointRouteBuilder MapVRChatPictureSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/vrchat-pictures")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(new VRChatPictureUploadsView(settings.VRChatPictureUploads, settings.VRChatHasPlus));
            })
            .WithName("GetVRChatPictureUploads")
            .WithSummary("Get VRChat picture uploads")
            .WithDescription("Whether the calendar's event form may upload a picture to VRChat.")
            .Produces<VRChatPictureUploadsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetVRChatPictureUploadsRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.VRChatPictureUploads;

                if (before == body.On)
                    return Results.Ok(new VRChatPictureUploadsView(before, settings.VRChatHasPlus));

                // Off is always allowed; on only when the account is not known to lack VRChat+.
                if (body.On && settings.VRChatHasPlus == false)
                {
                    return Results.Json(
                        new { error = "Picture uploads need VRChat+ on Modbot's VRChat account." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                settings.VRChatPictureUploads = body.On;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "vrchatPictureUploads",
                        ["before"] = before,
                        ["after"] = body.On,
                    },
                    ct);

                return Results.Ok(new VRChatPictureUploadsView(body.On, settings.VRChatHasPlus));
            })
            .WithName("SetVRChatPictureUploads")
            .WithSummary("Set VRChat picture uploads")
            .WithDescription(
                "Turn the calendar form's upload of a picture to VRChat on or off. "
                + "Turning it on is refused while the account is known to have no VRChat+.")
            .Produces<VRChatPictureUploadsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        // One read of Modbot's own VRChat user, for a person pressing Check again. The same
        // endpoint and lane as the session check and profile sync (users.read, interactive
        // priority): a 429 is a cold stop and is not retried, and nothing here loops.
        group.MapPost("/check-plus", async (
                [FromServices] ModbotContext db,
                // Optional: the VRChat services are wired by the host, not by the API.
                [FromServices] IVRChatGate? gate,
                HttpContext http,
                CancellationToken ct) =>
            {
                if (gate is null)
                {
                    return Results.Json(
                        new { error = "This build of Modbot cannot reach VRChat." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var settings = await db.GetSettingsAsync(ct);
                var userId = settings.VRChatSessionUserId ?? settings.VRChatAccountUserId;

                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Results.Json(
                        new { error = "Modbot has not signed in to VRChat yet." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                var result = await gate.ExecuteAsync(
                    new VRChatEndpoint(VRChatEndpointClass.UsersRead, null, "GetUser (VRChat+ check)"),
                    (vrchat, token) => vrchat.Users.GetUserWithHttpInfoAsync(userId, token),
                    VRChatCallPriority.Interactive,
                    ct);

                if (!result.Success)
                {
                    return Results.Json(
                        new { error = GroupPageAnswers.Said(result) },
                        statusCode: GroupPageAnswers.StatusFor(result));
                }

                if (VRChatPlus.FromTags(result.Value?.Tags) is not { } hasPlus)
                {
                    return Results.Json(
                        new { error = "VRChat did not say whether the account has VRChat+." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                // Saves the answer; with no VRChat+ it also turns Upload VRChat pictures off and records that.
                await VRChatPlusStatus.SaveAsync(http.RequestServices, db, settings, hasPlus, ct);

                return Results.Ok(new VRChatPictureUploadsView(settings.VRChatPictureUploads, hasPlus));
            })
            .WithName("CheckVRChatPlus")
            .WithSummary("Check VRChat+")
            .WithDescription(
                "Reads Modbot's own VRChat user again and keeps whether its tags say it has VRChat+. "
                + "Without VRChat+, Upload VRChat pictures is turned off. "
                + "One request to VRChat (users.read, interactive), never retried.")
            .Produces<VRChatPictureUploadsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
