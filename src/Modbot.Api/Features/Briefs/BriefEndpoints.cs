using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.AI;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.People;
using Modbot.Api.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Briefs;

/// <summary>
/// AI briefs: what Modbot recorded about one instance or one person, summarised by the Chat model
/// with the entry ids each line rests on, and no opinion (AI chat design §14).
/// </summary>
/// <remarks>
/// POST, not GET: each one is a paid call to the AI provider and a row in the call log, and a GET
/// is something a browser or a link preview may fetch on its own.
/// </remarks>
public static class BriefEndpoints
{
    /// <summary>
    /// How many entries a person's brief is built from: the newest, as the Activity tab lists them
    /// newest first. The same number an instance's is held to (<see cref="PlacesEndpoints.FactsListed"/>).
    /// </summary>
    public const int MostEntries = PlacesEndpoints.FactsListed;

    public static IEndpointRouteBuilder MapBriefs(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/briefs").WithTags("Briefs").RequireAuthorization();

        group.MapPost("/instances/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] InstanceBriefRequest? body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] AiSpendLimits limits,
                [FromServices] AiCallRunner runner,
                [FromServices] IAiCallLog calls,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                if (AskerOf(http) is not { } asker)
                    return Results.Forbid();

                var writer = new BriefWriter(db, ai, limits, runner, calls, clock, facts, partitions);

                try
                {
                    var settings = await writer.SettingsAsync(ct);

                    var view = await PlacesEndpoints.InstanceAsync(id, asker.Held, db, clock.UtcNow, ct);
                    if (view is null)
                        return Results.NotFound();

                    // The popup's own read of what happened there: empty without See the audit log,
                    // and bounded by the instance's own open and close times.
                    var (entries, newest) = await PlacesEndpoints.InstanceLogAsync(db, asker.Held, view, ct);

                    var row = view.Instance;
                    var place = BriefWriter.PlaceOf(row.WorldName ?? row.WorldId, row.VRChatInstanceId, row.InstanceName);
                    var zone = AI.Insights.InsightTimes.ZoneOrUtc(body?.TimeZone);
                    var until = row.ClosedAt is { } closed
                        ? $"to {AI.Briefs.BriefPrompt.Time(closed, zone)}"
                        : $"and last seen open at {AI.Briefs.BriefPrompt.Time(view.LastSeenAt, zone)}";

                    var subject = new BriefSubject(
                        $"the instance {place}, open from {AI.Briefs.BriefPrompt.Time(row.OpenedAt, zone)} {until}.",
                        entries,
                        newest,
                        BriefWriter.PeopleIn(entries),
                        new JsonObject { ["via"] = "brief", [BriefNotes.KindKey] = BriefNotes.InstanceBrief, ["instanceId"] = id.ToString() },
                        row.VRChatInstanceId is { Length: > 0 } number ? (row.WorldId, number) : null);

                    return Results.Ok(await writer.WriteAsync(settings, subject, asker, body?.TimeZone, ct));
                }
                catch (BriefRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.UseAiChat | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog)
            .WithName("WriteInstanceBrief")
            .WithSummary("Brief me on an instance")
            .WithDescription(
                "A summary, written by the Chat model, of what the audit log recorded in one "
                + "instance: the newest 100 entries the instance popup's Activity tab shows you, in "
                + "one call with no tools. Each line of `text` ends with the entry ids it rests on, "
                + "in square brackets; `sources` lists the cited ids that were among the entries "
                + "sent, and `builtFrom` says what it was built from. No opinions, recommendations, "
                + "scores or reasons.\n\n"
                + "Needs AI on, Chat on and AI briefs on (Settings → AI → Chat), and Use AI chat, "
                + "See analytics and See the audit log. Counted, limited and logged as a Chat call; "
                + "the call log keeps what was sent and what came back. With nothing recorded the "
                + "model is not asked and `text` is null. 409 when briefs are off or AI is not set "
                + "up, 429 at a spend limit or the monthly allowance, 502 when the provider did not "
                + "answer.")
            .Produces<BriefView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway);

        group.MapPost("/people", async (
                HttpContext http,
                [FromBody] PersonBriefRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] AiSpendLimits limits,
                [FromServices] AiCallRunner runner,
                [FromServices] IAiCallLog calls,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (AskerOf(http) is not { } asker)
                    return Results.Forbid();

                var given = new[]
                {
                    !string.IsNullOrWhiteSpace(body.VRChatUserId),
                    !string.IsNullOrWhiteSpace(body.DiscordUserId),
                    body.AccountId is not null,
                }.Count(set => set);

                if (given != 1)
                    return Results.BadRequest(new { error = "Give one of vrchatUserId, discordUserId or accountId." });

                // Nothing to read at all is the audit log's own answer, and so this one's.
                if (AuditVisibility.VisibleTypes(asker.Held).Count == 0)
                    return Results.Forbid();

                var writer = new BriefWriter(db, ai, limits, runner, calls, clock, facts, partitions);

                try
                {
                    var settings = await writer.SettingsAsync(ct);

                    var (named, platform) = body switch
                    {
                        { VRChatUserId: { } vrchatId } when !string.IsNullOrWhiteSpace(vrchatId) => (vrchatId.Trim(), FactPlatform.VRChat),
                        { DiscordUserId: { } discordId } when !string.IsNullOrWhiteSpace(discordId) => (discordId.Trim(), FactPlatform.Discord),
                        _ => (body.AccountId!.Value.ToString(), FactPlatform.Modbot),
                    };

                    // The accounts the Activity tab reads, tied the way it ties them: the audit
                    // log's own person filter, with the asker's own sight.
                    var ids = await PersonTimeline.ResolveAsync(db, clock, asker.Held, named, platform, ct);
                    var (entries, newest) = await PersonEntriesAsync(db, asker.Held, ids, clock.UtcNow, ct);

                    // Names for the brief's first line and the lookup entry, under the same sight.
                    var person = await new PersonLookup(db, clock).ResolveAsync(
                        new PersonAsk(
                            platform == FactPlatform.VRChat ? named : null,
                            platform == FactPlatform.Discord ? named : null,
                            platform == FactPlatform.Modbot ? body.AccountId : null),
                        PersonSight.Of(asker.Held),
                        ct);

                    var people = new List<LookedUpPerson>();
                    if (ids.VRChat is { } vrchat)
                        people.Add(new LookedUpPerson(FactPlatform.VRChat, vrchat, person.VRChat?.Name));
                    if (ids.Discord is { } discord)
                        people.Add(new LookedUpPerson(FactPlatform.Discord, discord, person.Discord?.Name));

                    var name = person.VRChat?.Name ?? person.Discord?.Name ?? person.Account?.Username ?? named;

                    var subject = new BriefSubject(
                        $"the person {name}: every entry about or by any of their accounts.",
                        entries,
                        newest,
                        people,
                        new JsonObject { ["via"] = "brief", [BriefNotes.KindKey] = BriefNotes.PersonBrief });

                    return Results.Ok(await writer.WriteAsync(settings, subject, asker, body.TimeZone, ct));
                }
                catch (BriefRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("WritePersonBrief")
            .WithSummary("Brief me on a person")
            .WithDescription(
                "A summary, written by the Chat model, of what the audit log recorded about one "
                + "person: the newest 100 entries the person popup's Activity tab shows you, about or "
                + "by any of their VRChat, Discord and Modbot accounts, in one call with no tools. "
                + "Give exactly one of `vrchatUserId`, `discordUserId` or `accountId`, as for "
                + "`GET /api/people/lookup`. The answer is shaped as for an instance's brief.\n\n"
                + "Needs AI on, Chat on and AI briefs on, and Use AI chat; which entries are read "
                + "follows your audit log permissions. A brief that read people records "
                + "`modbot.chat.lookup` with `via: brief`, as a Chat question does.")
            .Produces<BriefView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway);

        group.MapPost("/{callId:guid}/note", async (
                HttpContext http,
                [FromRoute] Guid callId,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (AskerOf(http) is not { } asker)
                    return Results.Forbid();

                try
                {
                    return Results.Ok(await new Notes.NoteService(db, clock, facts, partitions)
                        .SaveBriefAsync(callId, new Cases.Caller(asker.UserId, asker.Username, asker.Held), ct));
                }
                catch (Notes.NoteRefused refused)
                {
                    return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
                }
            })
            .RequiresFlag(ModbotPermissions.WriteNotes)
            .WithName("SaveBriefAsNote")
            .WithSummary("Save a brief as a note")
            .WithDescription(
                "Saves one of your person briefs as a note about that person, marked `writtenByAi`. "
                + "The server writes the note: the brief's text as the call log holds it, a blank "
                + "line, and its `builtFrom` line. `callId` is the brief's own; a brief about an "
                + "instance, one somebody else asked for, or one that did not answer is 404, and a "
                + "brief already saved is 409. Needs Write notes.")
            .Produces<Notes.NoteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// Every entry about or by any of the person's accounts, newest first: the person popup's
    /// Activity tab's own read, one page of it.
    /// </summary>
    internal static async Task<(IReadOnlyList<AuditEntry> Entries, bool Newest)> PersonEntriesAsync(
        ModbotContext db,
        ModbotPermissions held,
        PersonIds person,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var visible = AuditVisibility.VisibleTypes(held);
        if (visible.Count == 0)
            return ([], false);

        var page = await new AuditQuery(db, held).PageAsync(
            new AuditRequest(visible, [], null, null, null, null, null, null, null, MostEntries, Person: person),
            now,
            ct);

        return (page.Entries, page.Next is not null);
    }

    private static BriefAsker? AskerOf(HttpContext http)
    {
        var id = ModbotAuth.UserIdOf(http.User);
        return id is null
            ? null
            : new BriefAsker(id.Value, ModbotAuth.UsernameOf(http.User) ?? string.Empty, ModbotAuth.PermissionsOf(http.User));
    }
}
