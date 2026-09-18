using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.Core.Updates;

namespace Modbot.Api.Features.Settings;

/// <param name="Running">The release this server is running.</param>
/// <param name="Newest">The newest release there is, or null when Modbot has not been told yet.</param>
/// <param name="NewerAvailable">True when there is a later release than the one running.</param>
/// <param name="PublishedAt">When the newest release was published.</param>
/// <param name="NotesUrl">The page with its notes on it.</param>
/// <param name="Image">The image to pull for it.</param>
/// <param name="Tag">The tag to pull it with.</param>
/// <param name="CheckedAt">When the last check was made.</param>
/// <param name="Problem">What went wrong with the last check, or null.</param>
/// <param name="On">Whether this server checks for updates at all.</param>
public sealed record UpdateView(
    [property: JsonPropertyName("running")] string Running,
    [property: JsonPropertyName("newest")] string? Newest,
    [property: JsonPropertyName("newerAvailable")] bool NewerAvailable,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("notesUrl")] string? NotesUrl,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset? CheckedAt,
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("on")] bool On);

public sealed record SetUpdateCheckRequest([property: JsonPropertyName("on")] bool On);

/// <summary>
/// Whether this server asks what the newest Modbot release is, and what the answer was.
/// </summary>
/// <remarks>
/// <para>
/// The question sends nothing about this deployment, so <c>MODBOT_CLOUD_DISABLED</c> does not turn
/// it off — this switch is the one that does, for an operator who wants no outbound calls at all.
/// See <c>ModbotUpdateAddress</c>.
/// </para>
/// <para>
/// Modbot never updates itself and never pulls an image. This says what exists and what to pull;
/// the operator decides.
/// </para>
/// </remarks>
public static class UpdateSettingsEndpoints
{
    public static IEndpointRouteBuilder MapUpdateSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/updates")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // What was last learned. Never asks: the loop does the asking, so opening the page cannot
        // turn a screen refresh into a request to somebody else's service.
        group.MapGet("/", async (
                [FromServices] UpdateChecker checker,
                CancellationToken ct) => Results.Ok(Seen(await checker.ReadAsync(ct))))
            .WithName("GetUpdateCheck")
            .WithSummary("The newest Modbot release, and whether this server looks for it")
            .Produces<UpdateView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetUpdateCheckRequest body,
                [FromServices] UpdateChecker checker,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var before = await checker.ReadAsync(ct);

                if (before.On == body.On)
                    return Results.Ok(Seen(before));

                var after = await checker.SetAsync(body.On, ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "checkForUpdates",
                        ["before"] = before.On,
                        ["after"] = after.On,
                    },
                    ct);

                return Results.Ok(Seen(after));
            })
            .WithName("SetUpdateCheck")
            .WithSummary("Turn update checking on or off")
            .Produces<UpdateView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static UpdateView Seen(UpdateStatus status) => new(
        status.Running,
        status.Newest,
        status.NewerAvailable,
        status.PublishedAt,
        status.NotesUrl,
        status.Image,
        status.Tag,
        status.CheckedAt,
        status.Problem,
        status.On);
}
