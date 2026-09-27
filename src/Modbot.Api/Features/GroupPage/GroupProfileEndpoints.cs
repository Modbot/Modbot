using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// Changing the group's profile on VRChat from the VRChat page: the Overview's edit buttons and the
/// Settings tab both send it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One request to VRChat per Save, and only when something changed.</strong> The edit is
/// compared with what Modbot stored from the last group read; the fields that differ are sent, and
/// nothing is sent when none do. VRChat answers with the group as it now stands, which is stored
/// at once (<see cref="GroupInfoSync.RecordEdit"/>), so the page shows the new values without
/// waiting for the next poll and without a second request.
/// </para>
/// <para>
/// <strong>Nothing is recorded unless VRChat accepted it.</strong> Then one fact names who made the
/// change and each field's old and new value — the half of the story VRChat's own audit log cannot
/// tell, because there the change was made by Modbot's account (foundation §5.9.1).
/// </para>
/// </remarks>
public static class GroupProfileEndpoints
{
    public static IEndpointRouteBuilder MapGroupProfile(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group").WithTags("Group page").RequireAuthorization();

        group.MapPut("/profile", async (
                HttpContext http,
                [FromBody] GroupProfileEdit body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupProfile? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // A change with no author is not recorded, so it is not made.
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (vrchat is null || facts is null || partitions is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.GetSettingsAsync(ct);

                if (settings.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                var (edit, problem) = GroupPageRules.Tidy(body);
                if (edit is null)
                    return GroupPageAnswers.Invalid(problem!);

                var changes = GroupPageRules.Changes(StateOf(settings), edit);

                // Nothing differs from what is stored: nothing to send, nothing to record.
                if (changes.Count == 0)
                    return Results.Ok(await new GroupInfoQuery(db).RunAsync(clock.UtcNow, ct));

                var changed = changes.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);

                var request = new GroupProfileChange(
                    name: changed.Contains("name") ? edit.Name : null,
                    description: changed.Contains("description") ? edit.Description : null,
                    rules: changed.Contains("rules") ? edit.Rules : null,
                    languages: changed.Contains("languages") ? [.. edit.Languages!] : null,
                    links: changed.Contains("links") ? [.. edit.Links!] : null,
                    joinState: changed.Contains("joinState") ? GroupPageRules.JoinStateOf(edit.JoinState!) : null);

                var answer = await vrchat.UpdateAsync(groupId, request, ct);

                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "UpdateGroup", groupId, settings);

                var now = clock.UtcNow;

                var fields = new JsonObject();
                foreach (var (field, old, @new) in changes)
                    fields[field] = new JsonObject { ["old"] = old, ["new"] = @new };

                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    await GroupPageAnswers.WriteFactAsync(
                        facts, partitions, FactType.GroupProfileChanged, groupId, actor, now,
                        new JsonObject { ["changed"] = fields },
                        ct);

                    // VRChat's answer is the group as it now stands. An answer with no body still
                    // means VRChat accepted, and the next poll brings the page up to date.
                    if (answer.Value is { } saved)
                        GroupInfoSync.RecordEdit(settings, saved);

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await new GroupInfoQuery(db).RunAsync(now, ct));
            })
            .RequiresFlag(ModbotPermissions.EditGroupProfile)
            .WithName("UpdateGroupProfile")
            .WithSummary("Update group profile")
            .WithDescription(
                "Change the group's name, description, rules, languages, links or who can join, on "
                + "VRChat. Leave a field out (or null) to keep it. Only the fields that differ from "
                + "what Modbot stored are sent, and nothing is sent when none do. Limits are "
                + "VRChat's: a name of 3 to 64 characters, a description of up to 250, at most 3 "
                + "languages (VRChat's codes, such as `eng`) and at most 3 `http` or `https` links. "
                + "`joinState` is `open`, `request`, `invite` or `closed`. One request to VRChat, "
                + "never retried: a refusal answers with VRChat's own message, a rate limit with "
                + "429. Once VRChat accepts, the change is written to the audit log with who made "
                + "it and each field's old and new value, and the answer is the group as the page "
                + "now shows it.")
            .Produces<GroupInfo>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>The profile as the last group read left it, which an edit is compared with.</summary>
    private static GroupProfileState StateOf(Core.Data.Entities.Settings settings)
    {
        var snapshot = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot);

        return new GroupProfileState(
            snapshot?.Name ?? settings.ManagedGroupName,
            snapshot?.Description,
            snapshot?.Rules,
            settings.ManagedGroupLanguages ?? [],
            settings.ManagedGroupLinks ?? [],
            snapshot?.JoinState?.ToLowerInvariant());
    }
}
