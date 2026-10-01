using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Modbot.Analytics.Facts;
using Modbot.Api.Conventions;
using Modbot.Core.Data.Entities;
using Modbot.VRChat;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// What every write to the group's page does the same way: find the group, turn a refusal into
/// VRChat's own words, and write the fact that names who made the change.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the profile, the posts, the roles, the sent invites and the gallery, so each of those
/// is its VRChat call and its fact and nothing else.
/// </para>
/// <para>
/// <strong>A refusal is VRChat's message, and it is never retried.</strong> A 429 or a gate cold
/// stop answers 429, a deployment that cannot reach VRChat at all answers 503, and anything else
/// VRChat turned down answers 502 with what VRChat said — "You do not have permission to do that",
/// say, when Modbot's own VRChat account lacks the group permission. Pressing the button again is
/// the person's decision; nothing here does it for them (spec 4.3.1).
/// </para>
/// </remarks>
public static class GroupPageAnswers
{
    /// <summary>
    /// VRChat's own reason for a refusal, or the gate's words when VRChat gave none (a cold stop
    /// that never sent anything, a timeout, a Cloudflare block).
    /// </summary>
    public static string Said<T>(VRChatResult<T> result)
        => (result.Kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(result.RawResponse))
            ?? result.ErrorMessage
            ?? "VRChat did not answer.";

    /// <summary>The HTTP status a refusal is passed on with. See the class remarks.</summary>
    public static int StatusFor<T>(VRChatResult<T> result)
        => result.IsRateLimited || result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting
            ? StatusCodes.Status429TooManyRequests
            : result.Kind == VRChatFailureKind.NotConfigured
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;

    /// <summary>
    /// A refusal, as the web app reads one: the problem shape with <c>missingGroupPermission</c>
    /// beside it, and the status above. The second is set when VRChat answered 403 because Modbot's own VRChat account lacks
    /// the group permission <paramref name="operation"/> needs, so the page can name it and link to
    /// where it is granted (<see cref="VRChatGroupPermissions"/>).
    /// </summary>
    /// <param name="operation">The operation name the call gave its endpoint, such as <c>UpdateGroup</c>.</param>
    /// <param name="settings">For the account's roles and permissions as last read.</param>
    public static IResult Refused<T>(VRChatResult<T> result, string operation, string groupId, Core.Data.Entities.Settings? settings)
        => Problems.Of(
            StatusFor(result),
            Said(result),
            CodeFor(result),
            new
            {
                missingGroupPermission = VRChatGroupPermissions.Refusal(
                    result.StatusCode, result.Kind, operation, groupId, result.RawResponse, settings),
            });

    /// <summary>The error code a refusal is passed on with (API conventions design §2), beside <see cref="StatusFor"/>.</summary>
    public static string CodeFor<T>(VRChatResult<T> result)
        => StatusFor(result) switch
        {
            StatusCodes.Status429TooManyRequests => Problems.VRChatRateLimited,
            StatusCodes.Status503ServiceUnavailable => Problems.NotSetUp,
            _ => Problems.VRChatRefused,
        };

    /// <summary>A request Modbot turned down before asking VRChat anything.</summary>
    public static IResult Invalid(string message)
        => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>What a write answers when this deployment cannot act in VRChat at all.</summary>
    public static IResult NotSetUp()
        => Problems.Of(StatusCodes.Status503ServiceUnavailable, "This deployment is not set up to act in VRChat.", Problems.NotSetUp);

    /// <summary>What anything answers before a group has been chosen.</summary>
    public static IResult NoGroup()
        => Problems.Of(StatusCodes.Status409Conflict, "No VRChat group is set up yet.", Problems.NoGroup);

    /// <summary>
    /// The fact for a change VRChat accepted: about the group, by the Modbot account that asked,
    /// entered by hand. Written inside the caller's transaction, so it and the stored change land
    /// together or not at all.
    /// </summary>
    public static Task WriteFactAsync(
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        string type,
        string groupId,
        Guid actor,
        DateTimeOffset now,
        JsonObject data,
        CancellationToken ct)
        => WriteFactAsync(facts, partitions, type, groupId, actor, now, data, ct, subjectId: null);

    /// <summary>
    /// The same, about somebody other than the group: a cancelled invite is about the person who
    /// was invited, so it shows on their history. <paramref name="subjectId"/> is their VRChat id,
    /// passed through untouched (spec 3.1.1); null means the group.
    /// </summary>
    public static async Task WriteFactAsync(
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        string type,
        string groupId,
        Guid actor,
        DateTimeOffset now,
        JsonObject data,
        CancellationToken ct,
        string? subjectId)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        await partitions.EnsureForAsync(now, ct);

        await facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                // The group is the subject, as on VRChat's own group.update entries, unless the
                // caller named somebody else. Passed through untouched (spec 3.1.1).
                SubjectId = subjectId ?? groupId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = actor.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);
    }
}
