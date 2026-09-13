using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Client.Alerts;
using Modbot.Api.Features.Client.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Client.Context;

public sealed record RosterMemberDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags);

public sealed record InstanceContextDto(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("members")] IReadOnlyList<RosterMemberDto> Members);

public sealed record UserSummaryDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

/// <summary>
/// The overlay's two reads: who is in this instance, and what is known about one of them.
/// </summary>
/// <remarks>
/// <para><strong>Small on purpose.</strong> The profile summary is deliberately not the full web
/// profile — an overlay card shows prior actions, roles, join date and current flags, and nothing
/// that needs scrolling in a headset.</para>
/// <para><strong>Everything here is derived from this deployment's own fact log.</strong> No
/// VRChat call is made to answer an overlay read: a moderator glancing at a roster must not be
/// able to spend the group's shared API budget, and the answer has to arrive in the time a glance
/// takes.</para>
/// <para><strong>A pairing sees exactly one group's context</strong>, which is the same boundary
/// the client's local routing enforces, arriving from the other side.</para>
/// </remarks>
public static class ContextHandler
{
    /// <summary>
    /// How far back to look for the presence that makes up a roster. An instance that has been
    /// running longer than this is unusual; one whose roster needs more history than this is not
    /// a roster any more.
    /// </summary>
    public static readonly TimeSpan RosterWindow = TimeSpan.FromHours(12);

    /// <summary>The fact types that count as a moderation action against somebody.</summary>
    private static readonly string[] ModerationActions =
        [FactType.MemberKicked, FactType.MemberBanned];

    public static async Task<IResult> ContextAsync(
        int apiVersion,
        string? instanceId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!ClientApiVersion.IsSupported(apiVersion))
            return ClientApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (instanceId is not { Length: > 0 })
            return ClientApiErrors.Malformed("An instanceId is required.");

        // Asking for an instance's roster is a device saying where it is standing, and it is the
        // steadiest such signal there is -- an overlay re-reads this every twenty seconds whether
        // or not anything is happening, where a quiet instance produces no ingest batches at all.
        // It is what keeps a moderator watching a silent room still able to receive the one alert
        // that matters. No new authority is granted by taking it at face value: this token could
        // already read any of this deployment's instances, and the only consequence is which of
        // that group's own alerts it is offered.
        locations.Record(authentication.Device!.Id, instanceId, clock.UtcNow);

        var since = clock.UtcNow - RosterWindow;

        var presence = await database.Events
            .AsNoTracking()
            .Where(e => e.InstanceId == instanceId
                     && e.OccurredAt >= since
                     && (e.Type == FactType.InstanceJoined
                      || e.Type == FactType.InstanceLeft
                      || e.Type == FactType.InstancePresenceObserved))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.Type, e.SubjectId, e.OccurredAt, e.Data })
            .ToListAsync(ct);

        // Last event per person wins: somebody whose most recent mark in this instance is a leave
        // is not in the roster, and somebody who left and came back is.
        var present = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var fact in presence)
        {
            if (fact.Type == FactType.InstanceLeft)
                present.Remove(fact.SubjectId);
            else
                present[fact.SubjectId] = ReadDisplayName(fact.Data) ?? present.GetValueOrDefault(fact.SubjectId);
        }

        if (present.Count == 0)
            return Results.Ok(new InstanceContextDto(instanceId, []));

        var subjects = present.Keys.ToList();
        var priorActions = await CountPriorActionsAsync(database, subjects, ct);
        var members = await CurrentMembersAsync(database, subjects, ct);

        var roster = present
            .Select(entry => Describe(entry.Key, entry.Value, priorActions, members))
            .ToList();

        return Results.Ok(new InstanceContextDto(instanceId, roster));
    }

    public static async Task<IResult> UserAsync(
        int apiVersion,
        string subjectId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        ModbotContext database,
        CancellationToken ct)
    {
        if (!ClientApiVersion.IsSupported(apiVersion))
            return ClientApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (subjectId is not { Length: > 0 })
            return ClientApiErrors.Malformed("A subjectId is required.");

        // One pass over this subject's facts, which the subject index serves directly. Several
        // narrower queries would each be a round trip for a card that has to arrive in the time a
        // glance takes.
        var facts = await database.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == subjectId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.Type, e.OccurredAt, e.Data })
            .ToListAsync(ct);

        if (facts.Count == 0)
        {
            // Nothing on record is a perfectly good answer, and saying so is better than a 404 the
            // overlay would have to translate.
            return Results.Ok(new UserSummaryDto(subjectId, null, "Ordinary", 0, null, [], []));
        }

        var priorActions = facts.Count(f => ModerationActions.Contains(f.Type));
        var joinedAt = facts.FirstOrDefault(f => f.Type == FactType.MemberJoined)?.OccurredAt;
        var displayName = facts.LastOrDefault(f => ReadDisplayName(f.Data) is not null) is { } named
            ? ReadDisplayName(named.Data)
            : null;

        var roles = new List<string>();
        foreach (var fact in facts)
        {
            if (ReadString(fact.Data, "roleName") is not { Length: > 0 } role)
                continue;

            if (fact.Type == FactType.RoleGranted && !roles.Contains(role, StringComparer.Ordinal))
                roles.Add(role);
            else if (fact.Type == FactType.RoleRevoked)
                roles.Remove(role);
        }

        var isMember = facts.LastOrDefault(f => f.Type is FactType.MemberJoined or FactType.MemberLeft)
            is { Type: FactType.MemberJoined };

        return Results.Ok(new UserSummaryDto(
            subjectId,
            displayName,
            Standing(priorActions, isMember, roles.Count > 0),
            priorActions,
            joinedAt,
            Flags(priorActions),
            roles));
    }

    /// <summary>
    /// How many moderation actions each of these people already has against them. One grouped
    /// query rather than one per person: an instance can hold two hundred and forty.
    /// </summary>
    public static async Task<Dictionary<string, int>> CountPriorActionsAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken ct)
        => await database.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && subjectIds.Contains(e.SubjectId)
                     && (e.Type == FactType.MemberKicked || e.Type == FactType.MemberBanned))
            .GroupBy(e => e.SubjectId)
            .Select(g => new { SubjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.SubjectId, g => g.Count, StringComparer.Ordinal, ct);

    private static async Task<HashSet<string>> CurrentMembersAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken ct)
    {
        var membership = await database.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && subjectIds.Contains(e.SubjectId)
                     && (e.Type == FactType.MemberJoined || e.Type == FactType.MemberLeft))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.SubjectId, e.Type })
            .ToListAsync(ct);

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in membership)
        {
            if (fact.Type == FactType.MemberJoined)
                members.Add(fact.SubjectId);
            else
                members.Remove(fact.SubjectId);
        }

        return members;
    }

    private static RosterMemberDto Describe(
        string subjectId,
        string? displayName,
        Dictionary<string, int> priorActions,
        HashSet<string> members)
    {
        var actions = priorActions.GetValueOrDefault(subjectId);

        return new RosterMemberDto(
            subjectId,
            displayName,
            Standing(actions, members.Contains(subjectId), isStaff: false),
            actions,
            Flags(actions));
    }

    /// <summary>
    /// Flagged beats everything: a moderator glancing at a roster needs the row that matters, and
    /// somebody with prior actions who is also a member is still the row that matters.
    /// </summary>
    private static string Standing(int priorActions, bool isMember, bool isStaff) => (priorActions, isStaff, isMember) switch
    {
        ( > 0, _, _) => "Flagged",
        (_, true, _) => "Staff",
        (_, _, true) => "Member",
        _ => "Ordinary",
    };

    private static IReadOnlyList<string> Flags(int priorActions) => priorActions switch
    {
        0 => [],
        1 => ["1 prior action"],
        _ => [$"{priorActions} prior actions"],
    };

    /// <summary>
    /// Pulls one string out of a fact's <c>jsonb</c> payload.
    /// </summary>
    /// <remarks>
    /// Display names are mutable and collide, so they are read as history rather than used as
    /// identity — the id is the identity, always. Malformed payloads return null instead of
    /// throwing: one unreadable fact must not take out a roster.
    /// </remarks>
    private static string? ReadString(string? data, string property)
    {
        if (data is not { Length: > 2 })
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadDisplayName(string? data) => ReadString(data, "displayName");
}
