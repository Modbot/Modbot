using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Companion.HeadsUps;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Companion.Context;

public sealed record RosterMemberDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("eighteenPlus")] bool? EighteenPlus = null);

/// <param name="HeadsUps">
/// What stands in this instance (heads-ups, 2026-10-03), oldest first. Sent only with this
/// instance's roster, to a device that just named it. Older companions ignore it.
/// </param>
public sealed record InstanceContextDto(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("members")] IReadOnlyList<RosterMemberDto> Members,
    [property: JsonPropertyName("headsUps")] IReadOnlyList<HeadsUpDto>? HeadsUps = null);

public sealed record UserSummaryDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null);

/// <summary>
/// The overlay's two reads: who is in this instance, and what is known about one of them.
/// </summary>
/// <remarks>
/// <para><strong>Small on purpose.</strong> The profile summary is deliberately not the full web
/// profile — an overlay card shows prior actions, roles, join date and current flags, and nothing
/// that needs scrolling in a headset.</para>
/// <para><strong>Everything here is derived from this deployment's own records</strong>: the fact
/// log, the trust rank and the 18+ mark off the stored profile row, and the member list and roles
/// the syncs keep (<see cref="MembersAndStaff"/>). No VRChat call is made to answer an overlay read: a
/// moderator glancing at a roster must not be able to spend the group's shared API budget, and
/// the answer has to arrive in the time a glance takes.</para>
/// <para><strong>A pairing sees exactly one group's context</strong>, which is the same boundary
/// the client's local routing enforces, arriving from the other side.</para>
/// <para><strong>A roster is only believed while a moderator is watching.</strong> This used to be
/// "last fact per person wins" over twelve hours, and because nobody is told that anyone left once
/// the last moderator walks out, everyone that moderator last saw stayed "present" for up to twelve
/// hours -- after the instance had closed. The roster is now <see cref="InstanceWatching"/>'s: everyone
/// present, from facts reported during the current watch, and nobody at all when nobody is
/// watching. The Live page and the Discord card use the same rule.</para>
/// </remarks>
public static class ContextHandler
{
    /// <summary>
    /// How far back to look for the presence that makes up a roster. An instance that has been
    /// watched without a break for longer than this is unusual; one whose roster needs more history
    /// than this is not a roster any more.
    /// </summary>
    public static readonly TimeSpan RosterWindow = TimeSpan.FromHours(12);

    public static async Task<IResult> ContextAsync(
        int apiVersion,
        string? instanceId,
        string? worldId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        HeadsUpSignal headsUpSignal,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (instanceId is not { Length: > 0 })
            return CompanionApiErrors.Malformed("An instanceId is required.");

        // An instance is its world and its number, and a number alone is only unique inside one
        // world. A client that sends no world (one built before this was asked for) gets the number
        // alone, as it always did, and so does one that sends it empty.
        var world = worldId is { Length: > 0 } ? worldId : null;

        // Asking for an instance's roster is a device saying where it is standing, and it is the
        // steadiest such signal there is -- an overlay re-reads this every twenty seconds whether
        // or not anything is happening, where a quiet instance produces no ingest batches at all.
        // It is what keeps a moderator watching a silent instance still able to receive the one alert
        // that matters. No new authority is granted by taking it at face value: this token could
        // already read any of this deployment's instances, and the only consequence is which of
        // that group's own alerts it is offered.
        locations.Record(authentication.Device!.Id, instanceId, clock.UtcNow, world);

        var since = clock.UtcNow - RosterWindow;

        // Whether the instance this address names has closed. Every row with the number (and the
        // world, when one was sent) that could still matter is consulted: if any is open, the
        // instance is open. An address whose rows have all closed ends every watch at the latest close.
        var closes = await database.VRChatInstances
            .AsNoTracking()
            .Where(i => i.VRChatInstanceId == instanceId
                && (world == null || i.WorldId == world)
                && (i.ClosedAt == null || i.ClosedAt >= since))
            .Select(i => i.ClosedAt)
            .ToListAsync(ct);

        DateTimeOffset? closedAt = closes.Count > 0 && closes.All(c => c is not null) ? closes.Max() : null;

        var people = await new InstancePeopleReader(database).ForNumberAsync(instanceId, world, since, closedAt, ct);

        // The heads-ups that stand here, after ending the ones this roster says are over: all of
        // them when the instance has closed or emptied, a message on somebody who has left. The
        // other staff here are told when one ends, as they are when one is cleared.
        var deviceId = authentication.Device!.Id;
        var (headsUps, ended) = await HeadsUpsHandler.StandingAsync(
            database,
            deviceId,
            instanceId,
            world,
            people.Here.Select(p => p.UserId).ToHashSet(StringComparer.Ordinal),
            closedAt is not null || people.Here.Count == 0,
            clock.UtcNow,
            ct);

        if (ended)
            HeadsUpsHandler.TellOthers(locations, headsUpSignal, deviceId, instanceId, world, clock.UtcNow);

        if (people.Here.Count == 0)
            return Results.Ok(new InstanceContextDto(instanceId, [], headsUps));

        var subjects = people.Here.Select(p => p.UserId).ToList();
        var members = await MembersAndStaff.ReadAsync(database, subjects, ct);
        var ranks = await TrustRanksAsync(database, subjects, ct);
        var eighteenPlus = await EighteenPlusAsync(database, subjects, ct);
        var flagged = await FlagRules.ReadAsync(database, subjects, ranks, clock.UtcNow, ct);

        var roster = people.Here
            .Select(person => Describe(
                person.UserId,
                person.DisplayName,
                flagged,
                members,
                ranks.GetValueOrDefault(person.UserId),
                eighteenPlus.TryGetValue(person.UserId, out var marked) ? marked : null))
            .ToList();

        return Results.Ok(new InstanceContextDto(instanceId, roster, headsUps));
    }

    public static async Task<IResult> UserAsync(
        int apiVersion,
        string subjectId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (subjectId is not { Length: > 0 })
            return CompanionApiErrors.Malformed("A subjectId is required.");

        var trustRank = await database.VRChatUsers
            .AsNoTracking()
            .Where(u => u.UserId == subjectId)
            .Select(u => u.TrustRank)
            .FirstOrDefaultAsync(ct);

        // One pass over this subject's facts, which the subject index serves directly. Several
        // narrower queries would each be a round trip for a card that has to arrive in the time a
        // glance takes.
        var facts = await database.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == subjectId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.Type, e.OccurredAt, e.Data })
            .ToListAsync(ct);

        // Read even with no facts on record: a Nuisance rank or a flag on a linked Discord account
        // still makes somebody Flagged.
        var match = (await FlagRules.ReadAsync(
                database, [subjectId], new Dictionary<string, TrustRank?> { [subjectId] = trustRank }, clock.UtcNow, ct))
            .GetValueOrDefault(subjectId) ?? FlagMatch.None;

        // Asked the way the roster asks, so the card and the row it opened from never disagree.
        var belonging = await MembersAndStaff.ReadAsync(database, [subjectId], ct);
        var standing = Standing(match, belonging.IsMember(subjectId), belonging.IsStaff(subjectId));

        if (facts.Count == 0)
        {
            // Nothing on record is a perfectly good answer, and saying so is better than a 404 the
            // overlay would have to translate.
            return Results.Ok(new UserSummaryDto(
                subjectId, null, standing, match.PriorActions, null,
                match.Reasons, [], trustRank));
        }

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

        return Results.Ok(new UserSummaryDto(
            subjectId,
            displayName,
            standing,
            match.PriorActions,
            joinedAt,
            match.Reasons,
            roles,
            trustRank));
    }

    /// <summary>The stored trust rank of each of these people whose tags have been read. One query.</summary>
    internal static async Task<Dictionary<string, TrustRank?>> TrustRanksAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken ct)
        => await database.VRChatUsers
            .AsNoTracking()
            .Where(u => subjectIds.Contains(u.UserId) && u.TrustRank != null)
            .Select(u => new { u.UserId, u.TrustRank })
            .ToDictionaryAsync(u => u.UserId, u => u.TrustRank, StringComparer.Ordinal, ct);

    /// <summary>
    /// Whether each of these people carries Modbot's 18+ mark, for everybody VRChat has told Modbot
    /// the age status of, or who has been marked by hand. One query.
    /// </summary>
    /// <remarks>
    /// The mark, not VRChat's own status as last seen: it is what the website and Discord show, and
    /// it stays once seen even if the person hides it again (user profile sync design §4). Somebody
    /// whose status has never been read is left out rather than sent as false, so a client can tell
    /// "not 18+" from "not known yet".
    /// </remarks>
    internal static async Task<Dictionary<string, bool>> EighteenPlusAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken ct)
        => await database.VRChatUsers
            .AsNoTracking()
            .Where(u => subjectIds.Contains(u.UserId) && (u.AgeVerificationStatus != null || u.Is18PlusVerified))
            .Select(u => new { u.UserId, u.Is18PlusVerified })
            .ToDictionaryAsync(u => u.UserId, u => u.Is18PlusVerified, StringComparer.Ordinal, ct);

    /// <param name="flagged">What <see cref="FlagRules"/> decided for everybody being described.</param>
    /// <param name="belonging">What <see cref="MembersAndStaff"/> read for everybody being described.</param>
    internal static RosterMemberDto Describe(
        string subjectId,
        string? displayName,
        IReadOnlyDictionary<string, FlagMatch> flagged,
        MembersAndStaff belonging,
        TrustRank? trustRank = null,
        bool? eighteenPlus = null)
    {
        var match = flagged.GetValueOrDefault(subjectId) ?? FlagMatch.None;

        return new RosterMemberDto(
            subjectId,
            displayName,
            Standing(match, belonging.IsMember(subjectId), belonging.IsStaff(subjectId)),
            match.PriorActions,
            match.Reasons,
            trustRank,
            eighteenPlus);
    }

    /// <summary>
    /// Flagged beats everything: a moderator glancing at a roster needs the row that matters, and
    /// somebody a rule flags who is also a member is still the row that matters.
    /// </summary>
    private static string Standing(FlagMatch match, bool isMember, bool isStaff) => (match.IsFlagged, isStaff, isMember) switch
    {
        (true, _, _) => "Flagged",
        (_, true, _) => "Staff",
        (_, _, true) => "Member",
        _ => "Ordinary",
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
