using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="On">Whether the calendar's event form may upload a picture to VRChat.</param>
public sealed record VRChatPictureUploadsView([property: JsonPropertyName("on")] bool On);

public sealed record SetVRChatPictureUploadsRequest([property: JsonPropertyName("on")] bool On);

/// <summary>
/// Whether the calendar's event form may upload a picture to VRChat, on the VRChat account Modbot
/// signs in as (calendar design §2).
/// </summary>
/// <remarks>
/// Off by default, on every install: VRChat answers the upload with a refusal unless the account
/// Modbot signs in as has VRChat+. Off, <c>POST /api/calendar/vrchat-picture</c> answers "Picture
/// uploads are off." and VRChat is never asked; events keep the picture ids they already have, and
/// the form takes a <c>file_</c> id typed by hand, as it did before uploads existed.
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
                return Results.Ok(new VRChatPictureUploadsView(settings.VRChatPictureUploads));
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
                    return Results.Ok(new VRChatPictureUploadsView(before));

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

                return Results.Ok(new VRChatPictureUploadsView(body.On));
            })
            .WithName("SetVRChatPictureUploads")
            .WithSummary("Set VRChat picture uploads")
            .WithDescription("Turn the calendar form's upload of a picture to VRChat on or off.")
            .Produces<VRChatPictureUploadsView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
