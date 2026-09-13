using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// The merged timeline, the filter vocabulary behind it, and the ban list derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.9.5: one timeline with source filter chips on by default, because the questions people
/// actually ask span sources — <em>"who changed the ban threshold just before these bans?"</em> is
/// unanswerable in either log alone.
/// </para>
/// <para>
/// <strong>Every parameter is explicitly attributed.</strong> Minimal APIs infer an unattributed
/// concrete type as the request body, and on a GET that does not merely misbehave: it throws while
/// the route is being mapped and takes every other endpoint in the host down with it. That has
/// already cost twelve unrelated test failures once.
/// </para>
/// <para>
/// The endpoints are authorised by hand rather than with <c>RequiresFlag</c>, because the answer
/// is not a yes or a no. A caller holding one of the two permissions gets that half of the log;
/// only a caller holding neither is refused. <c>RequiresFlag</c> requires every listed flag, which
/// would lock out exactly the moderator spec 5.9.4 is written for.
/// </para>
/// </remarks>
public static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLog(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/audit").WithTags("Audit").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromQuery(Name = "type")] string[]? types,
                [FromQuery(Name = "source")] string[]? sources,
                [FromQuery] string? subject,
                [FromQuery] string? subjectPlatform,
                [FromQuery] string? actor,
                [FromQuery] string? actorPlatform,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] DateTimeOffset? beforeOccurredAt,
                [FromQuery] long? beforeId,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);
                var visible = AuditVisibility.Resolve(held, ParseTypes(types));

                if (AuditVisibility.VisibleTypes(held).Count == 0)
                    return Results.Forbid();

                // A cursor is only a cursor with both halves. Half of one would page from a
                // timestamp with no tie-break and quietly drop every entry sharing that second.
                var cursor = beforeOccurredAt is { } at && beforeId is { } id
                    ? new AuditCursor(at, id)
                    : null;

                var request = new AuditRequest(
                    visible,
                    ParseSources(sources),
                    Trimmed(subject),
                    ParsePlatform(subjectPlatform),
                    Trimmed(actor),
                    ParsePlatform(actorPlatform),
                    from,
                    to,
                    cursor,
                    limit ?? AuditQuery.DefaultLimit);

                // Nothing visible left after the intersection: an honest empty page with the
                // coverage still attached, not a 403 for asking.
                if (visible.Count == 0)
                    return Results.Ok(new AuditPage([], null, await new AuditQuery(db).CoverageAsync([], ct)));

                return Results.Ok(await new AuditQuery(db).PageAsync(request, ct));
            })
            .WithName("GetAuditLog")
            .WithSummary("The merged fact timeline: who did what to whom, and when")
            .WithDescription(
                "One log, discriminated by source (spec 5.9). ViewAuditLog covers moderation and "
                + "membership history; ViewOperationalLog covers Modbot's own record — logins, "
                + "settings changes, sync failures. The two are separate permissions and the "
                + "filter is applied server-side on every query: asking for a type you cannot "
                + "see omits it rather than refusing the request.\n\n"
                + "Facts carry occurredAt and, when the time is an inference rather than a "
                + "statement, occurredBefore. `precision` says which, so a window is never "
                + "rendered as an instant.\n\n"
                + "Paged by keyset. Send the returned `next` back as beforeOccurredAt + beforeId; "
                + "both are required.")
            .Produces<AuditPage>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/filters", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);
                var visible = AuditVisibility.VisibleTypes(held);

                if (visible.Count == 0)
                    return Results.Forbid();

                var actors = await new AuditQuery(db)
                    .ActorsAsync(visible, clock.UtcNow - AuditQuery.ActorWindow, ct);

                return Results.Ok(new AuditFilters(
                    visible.Select(t => new AuditTypeOption(
                        t.ToString(),
                        FactLabels.For(t),
                        AuditVisibility.CategoryOf(t))).ToList(),
                    Enum.GetValues<FactSource>().Select(s => s.ToString()).ToList(),
                    actors,
                    AuditVisibility.CanSee(held, AuditCategory.Moderation),
                    AuditVisibility.CanSee(held, AuditCategory.Operational)));
            })
            .WithName("GetAuditFilters")
            .WithSummary("What this caller may filter by")
            .WithDescription(
                "The type list is already narrowed to the caller's permissions, so the SPA offers "
                + "only what the server would answer. The actor list is the recent actors in the "
                + "visible history — a filter control, deliberately not a member list.")
            .Produces<AuditFilters>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/bans", async (
                [FromServices] ModbotContext db,
                [FromQuery] int? offset,
                [FromQuery] int? limit,
                [FromQuery] bool? includeUnbanned,
                CancellationToken ct) =>
                Results.Ok(await new BanList(db).ListAsync(
                    offset ?? 0,
                    limit ?? BanList.DefaultLimit,
                    includeUnbanned ?? true,
                    ct)))
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithName("GetBanList")
            .WithSummary("Bans Modbot has recorded — not the group's ban list")
            .WithDescription(
                "Derived from ban and unban facts, which come from VRChat's group audit log. It "
                + "therefore covers the period since this deployment first synced, bounded by "
                + "whatever VRChat's own audit-log retention still held at that moment — a group "
                + "with three years of bans and a week-old Modbot has a week of them here.\n\n"
                + "`coverage` carries that window and callers must show it. A full ban list needs "
                + "a sweep of groups.bans, which is not built.")
            .Produces<BanListResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Reads enum names out of the query string, dropping anything unrecognised.
    /// </summary>
    /// <remarks>
    /// Silent rather than a 400, because these are display filters: a stale bookmark naming a
    /// type that has since been renamed should show the rest of the timeline, not an error page.
    /// Nothing is widened by a dropped name — the permission filter runs afterwards regardless.
    /// </remarks>
    private static List<T> ParseEnums<T>(string[]? values) where T : struct, Enum
    {
        if (values is null || values.Length == 0)
            return [];

        return values
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => Enum.TryParse<T>(v, ignoreCase: true, out var parsed) ? parsed : (T?)null)
            .OfType<T>()
            .Distinct()
            .ToList();
    }

    private static List<FactType> ParseTypes(string[]? values) => ParseEnums<FactType>(values);

    private static List<FactSource> ParseSources(string[]? values) => ParseEnums<FactSource>(values);

    private static FactPlatform? ParsePlatform(string? value)
        => Enum.TryParse<FactPlatform>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
