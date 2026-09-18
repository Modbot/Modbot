using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Retention;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// The other account a proved link ties to this one. Named so the operator knows it is there;
/// a purge never follows it.
/// </summary>
public sealed record PurgeLinkedAccountView(string Platform, string SubjectId, string? Name);

/// <summary>
/// What a purge would remove and what it would keep, counted before anything is destroyed.
/// </summary>
/// <param name="Platform">"VRChat" or "Discord".</param>
/// <param name="SubjectId">The id asked about, exactly as it was given.</param>
/// <param name="Name">The display name last seen, or null.</param>
/// <param name="IsMember">In the group or server right now. Null when no membership row was ever seen.</param>
/// <param name="IsBanned">On the group's ban list right now. Null for a Discord account.</param>
/// <param name="Facts">Facts about them.</param>
/// <param name="CountedDailyTotals">Counted-only daily total rows dimensioned on them.</param>
/// <param name="Days">Days whose totals are worked out again afterwards.</param>
/// <param name="Messages">Discord messages they wrote.</param>
/// <param name="GiveawayEntries">Standing giveaway entries.</param>
/// <param name="GiveawayPlaces">Places in a past draw that lose their name and ids.</param>
/// <param name="ImportRecords">Rows saying a record about them came from an uploaded file.</param>
/// <param name="CaseFilesKept">Case files about them. Kept.</param>
/// <param name="EvidenceFilesKept">Evidence files on those case files. Kept.</param>
/// <param name="LinkedAccount">Their other account, when a link proves one.</param>
public sealed record PurgePreviewResponse(
    string Platform,
    string SubjectId,
    string? Name,
    bool? IsMember,
    bool? IsBanned,
    int Facts,
    int CountedDailyTotals,
    int Days,
    int Messages,
    int GiveawayEntries,
    int GiveawayPlaces,
    int ImportRecords,
    int CaseFilesKept,
    int EvidenceFilesKept,
    PurgeLinkedAccountView? LinkedAccount);

/// <param name="Platform">"VRChat" or "Discord".</param>
/// <param name="SubjectId">Who to erase. Opaque text, never parsed (foundation §3.1.1).</param>
/// <param name="Confirmation">
/// The same id again, typed by the person pressing the button. It must match exactly.
/// </param>
public sealed record PurgeRequest(string Platform, string SubjectId, string Confirmation);

/// <summary>
/// What the purge destroyed, and what it kept and why (evidence storage design §15.2).
/// </summary>
/// <param name="Facts">Facts erased.</param>
/// <param name="CountedDailyTotals">Counted-only daily total rows erased.</param>
/// <param name="Days">Days whose totals were worked out again.</param>
/// <param name="Messages">Discord messages erased, earlier texts of edited ones included.</param>
/// <param name="GiveawayEntries">Standing giveaway entries removed.</param>
/// <param name="GiveawayPlaces">Places in a past draw whose name and ids were erased.</param>
/// <param name="CaseFilesKept">Case files kept.</param>
/// <param name="EvidenceFilesKept">Evidence files kept.</param>
public sealed record PurgeReceipt(
    int Facts,
    int CountedDailyTotals,
    int Days,
    int Messages,
    int GiveawayEntries,
    int GiveawayPlaces,
    int CaseFilesKept,
    int EvidenceFilesKept);

/// <summary>
/// Settings → Purge a person: everything Modbot stores about one person, removed on request.
/// </summary>
/// <remarks>
/// <para>
/// Foundation §5.5 has promised since the first draft that "deletion works", and
/// <see cref="IUserPurger"/> has implemented it since M2 with nothing able to call it. This is the
/// way in, and it is deliberately the only one.
/// </para>
/// <para>
/// <strong>Administrator, not a permission of its own.</strong> Evidence storage design §14 already
/// settled this in the table of three operations: detach needs Manage evidence, destroy needs
/// Destroy evidence, and purge-user needs Administrator. A new flag would be a way to hand the
/// single most consequential action in Modbot to somebody who holds nothing else, which is the
/// opposite of what a narrow permission is for.
/// </para>
/// <para>
/// <strong>The typed id is the confirmation, and there is no written reason.</strong> Destroying
/// evidence requires one because that record survives, attached to a case the group may have to
/// defend. A purge's record must not carry text that could name the person it erased, and a free
/// text box on this screen is the one field where a moderator would type their name. So the only
/// thing typed is the id itself, and it is checked and then thrown away.
/// </para>
/// <para>
/// No single-use key either, unlike a ban (moderation §3.1). That key exists because two bans are
/// worse than one; a second purge of the same person finds nothing left and removes nothing, so
/// the double-press it would guard against is already harmless.
/// </para>
/// </remarks>
public static class PurgeEndpoints
{
    public static IEndpointRouteBuilder MapPurge(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/purge").WithTags("Settings").RequireAuthorization();

        group.MapGet("", async (
                [FromServices] PurgePreviewer previewer,
                [FromQuery] string? platform,
                [FromQuery] string? subjectId,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(subjectId))
                    return Results.BadRequest(new { error = "An id is required." });

                if (!TryPlatform(platform, out var parsed))
                    return Results.BadRequest(new { error = "`platform` is VRChat or Discord." });

                return Results.Ok(View(await previewer.PreviewAsync(parsed, subjectId.Trim(), ct)));
            })
            .RequiresFlag(ModbotPermissions.Administrator)
            .WithName("PreviewPurge")
            .WithSummary("What removing everything about one person would destroy, and what it would keep")
            .WithDescription(
                "Counts only; nothing is changed. Every number is counted from the same tables "
                + "the purge writes to. A field Modbot cannot answer for a platform is null "
                + "rather than zero: a Discord account has no group ban list and no case files.")
            .Produces<PurgePreviewResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("", async (
                HttpContext http,
                PurgeRequest body,
                [FromServices] PurgePreviewer previewer,
                [FromServices] IUserPurger purger,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.SubjectId))
                    return Results.BadRequest(new { error = "An id is required." });

                if (!TryPlatform(body.Platform, out var platform))
                    return Results.BadRequest(new { error = "`platform` is VRChat or Discord." });

                var subjectId = body.SubjectId.Trim();

                // Ordinal, and not trimmed on the typed side either: the id is opaque text and
                // "close enough" is not a standard to erase somebody by.
                if (!string.Equals(body.Confirmation, subjectId, StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "The typed id does not match." });

                // Counted first, inside the same request: after the purge there is nothing left
                // to ask, and the kept counts are the half of the receipt §15.2 insists on.
                var before = await previewer.PreviewAsync(platform, subjectId, ct);

                var result = await purger.PurgeAsync(platform, subjectId, Actor(http), ct);

                return Results.Ok(new PurgeReceipt(
                    result.FactsDeleted,
                    result.CountedDailyTotalsDeleted,
                    result.DaysRecomputed,
                    result.MessagesDeleted,
                    result.GiveawayEntriesDeleted,
                    result.GiveawayEntrantsBlanked,
                    before.CaseFilesKept,
                    before.EvidenceFilesKept));
            })
            .RequiresFlag(ModbotPermissions.Administrator)
            .WithName("PurgePerson")
            .WithSummary("Remove everything Modbot stores about one person")
            .WithDescription(
                "Irreversible. `confirmation` must equal `subjectId` exactly.\n\n"
                + "Erased: every fact where they are the subject, the counted-only daily totals "
                + "dimensioned on them, the rows saying a record about them was imported, their "
                + "Discord messages including earlier texts of edited ones, and their standing "
                + "giveaway entries. Their place in a past draw keeps its position and weight and "
                + "loses its name and ids, so an old result still checks out.\n\n"
                + "Kept: facts where they were the actor rather than the subject — somebody "
                + "else's moderation history — case files about them with their evidence and "
                + "profile snapshots (evidence storage design §15.1), the current-state rows the "
                + "next sync would write again, and the fact that this purge happened.\n\n"
                + "A purge of somebody Modbot has never seen removes nothing and is not an error.")
            .Produces<PurgeReceipt>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>The signed-in account, for the fact the purge leaves behind.</summary>
    private static PurgeActor? Actor(HttpContext http)
    {
        var id = ModbotAuth.UserIdOf(http.User);

        return id is null
            ? null
            : new PurgeActor(id.Value, http.User.Identity?.Name ?? "unknown");
    }

    /// <summary>
    /// The platform by name, case-insensitively. Modbot is not accepted: it is Modbot's own word
    /// for itself as the actor of its own records, and nobody is a person on it.
    /// </summary>
    private static bool TryPlatform(string? name, out FactPlatform platform)
    {
        platform = default;

        if (string.Equals(name, nameof(FactPlatform.VRChat), StringComparison.OrdinalIgnoreCase))
            platform = FactPlatform.VRChat;
        else if (string.Equals(name, nameof(FactPlatform.Discord), StringComparison.OrdinalIgnoreCase))
            platform = FactPlatform.Discord;
        else
            return false;

        return true;
    }

    private static PurgePreviewResponse View(PurgePreview p)
        => new(
            p.Platform.ToString(),
            p.SubjectId,
            p.Name,
            p.IsMember,
            p.IsBanned,
            p.Facts,
            p.CountedDailyTotals,
            p.Days,
            p.Messages,
            p.GiveawayEntries,
            p.GiveawayPlaces,
            p.ImportRecords,
            p.CaseFilesKept,
            p.EvidenceFilesKept,
            p.LinkedAccount is null
                ? null
                : new PurgeLinkedAccountView(
                    p.LinkedAccount.Platform.ToString(),
                    p.LinkedAccount.SubjectId,
                    p.LinkedAccount.Name));
}
