using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Companion.HeadsUps;
using Modbot.Api.Features.Files;
using Modbot.Api.Features.Notes;
using Modbot.Api.Features.Places;
using Modbot.Api.Features.ProblemAvatars;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat;
using Modbot.VRChat.Files;

namespace Modbot.Api.Features.Companion.Context;

/// <param name="PictureUrl">
/// Where this server serves the person's picture to a paired device, as a path on this server
/// (<see cref="PictureRoute"/>), or null when the server holds no picture for them or the operator
/// has switched VRChat pictures off. Never a VRChat address: the companion asks this server for the
/// picture and never VRChat. Older companions ignore it.
/// </param>
/// <param name="IsMember">
/// Whether they are a member of the group now, by the rule the Member standing uses. Sent apart from
/// <paramref name="Standing"/> because that holds one word, and Staff and Flagged hide it. Older
/// companions ignore it.
/// </param>
/// <param name="Invitation">
/// For somebody who is not a member: <c>Invited</c> (the group invited them and nothing has answered it),
/// <c>AskedToJoin</c> (a join request nobody has answered) or <c>NotInvited</c>. Null for a member. Older
/// companions ignore it.
/// </param>
/// <param name="CompanionRunning">
/// Whether this person's own companion is running now: a paired device of theirs that is not revoked
/// has been heard from within <see cref="ContextHandler.CompanionRunningWindow"/>. False for everybody
/// else, including somebody with no paired companion. Older companions ignore it.
/// </param>
public sealed record RosterMemberDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("eighteenPlus")] bool? EighteenPlus = null,
    [property: JsonPropertyName("pictureUrl")] string? PictureUrl = null,
    [property: JsonPropertyName("isMember")] bool? IsMember = null,
    [property: JsonPropertyName("invitation")] string? Invitation = null,
    [property: JsonPropertyName("companionRunning")] bool? CompanionRunning = null);

/// <param name="HeadsUps">
/// What stands in this instance (heads-ups, 2026-10-03), oldest first. Sent only with this
/// instance's roster, to a device that just named it. Older companions ignore it.
/// </param>
public sealed record InstanceContextDto(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("members")] IReadOnlyList<RosterMemberDto> Members,
    [property: JsonPropertyName("headsUps")] IReadOnlyList<HeadsUpDto>? HeadsUps = null);

/// <param name="TimesSeen">
/// How many times a moderator's client saw them arrive (the web profile's Arrivals). Null when
/// they were never seen. Older companions ignore it.
/// </param>
/// <param name="FirstSeenAt">When they were first seen in an instance (the web profile's First seen), or null.</param>
/// <param name="NoteCount">How many notes stand on them, not counting ones taken back. Null when there are none. Only the number: what a note says is the audit log's permission.</param>
/// <param name="LastAction">
/// The last thing this group did to them: <c>Kick</c>, <c>Ban</c>, <c>Unban</c> or <c>Warn</c>. Null
/// when nothing was.
/// </param>
/// <param name="LastActionAt">When <paramref name="LastAction"/> happened.</param>
/// <param name="Watched">Whether somebody is watching them now. The word only, never why (<see cref="WatchHit"/>).</param>
/// <param name="AvatarName">
/// The avatar they were last seen wearing, exactly as VRChat's log named it. Null when none was ever
/// seen, and always null for a device whose owner may not manage settings: only they may mark it.
/// </param>
/// <param name="AvatarListed">Whether <paramref name="AvatarName"/> is on the problem avatar list. Null when the avatar is.</param>
/// <param name="Invitation">As <see cref="RosterMemberDto.Invitation"/>.</param>
/// <param name="CompanionRunning">As <see cref="RosterMemberDto.CompanionRunning"/>.</param>
public sealed record UserSummaryDto(
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("standing")] string Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("trustRank")] TrustRank? TrustRank = null,
    [property: JsonPropertyName("timesSeen")] int? TimesSeen = null,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset? FirstSeenAt = null,
    [property: JsonPropertyName("noteCount")] int? NoteCount = null,
    [property: JsonPropertyName("lastAction")] string? LastAction = null,
    [property: JsonPropertyName("lastActionAt")] DateTimeOffset? LastActionAt = null,
    [property: JsonPropertyName("watched")] bool? Watched = null,
    [property: JsonPropertyName("avatarName")] string? AvatarName = null,
    [property: JsonPropertyName("avatarListed")] bool? AvatarListed = null,
    [property: JsonPropertyName("invitation")] string? Invitation = null,
    [property: JsonPropertyName("companionRunning")] bool? CompanionRunning = null);

/// <summary>
/// The overlay's two reads: who is in this instance, and what is known about one of them.
/// </summary>
/// <remarks>
/// <para><strong>Small on purpose.</strong> The profile summary is deliberately not the full web
/// profile — an overlay card shows prior actions, roles, join date, times seen, notes and current flags, and nothing
/// that needs scrolling in a headset.</para>
/// <para><strong>Everything here is derived from this deployment's own records</strong>: the fact
/// log, the trust rank and the 18+ mark off the stored profile row, and the member list and roles
/// the syncs keep (<see cref="MembersAndStaff"/>). No VRChat call is made to answer an overlay read: a
/// moderator glancing at a roster must not be able to spend the group's shared API budget, and
/// the answer has to arrive in the time a glance takes. That holds for pictures too: the roster
/// names, for each person whose picture this server's cache already holds, the path it is served
/// at (<see cref="PictureAsync"/>), and that route sends only what the cache holds. Nothing here
/// makes a request to VRChat, of any kind.</para>
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

    /// <summary>
    /// How recently a paired device must have been heard from for its owner's companion to count as
    /// running. A device's last-seen time is written at most once every
    /// <see cref="DeviceStanding.SeenWriteEvery"/> (five minutes), so a running companion can show a
    /// last-seen up to five minutes old; two more minutes are added so one slow write does not make
    /// the icon blink off.
    /// </summary>
    public static readonly TimeSpan CompanionRunningWindow = DeviceStanding.SeenWriteEvery + TimeSpan.FromMinutes(2);

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
        CancellationToken ct,
        [FromServices] VRChatFileCache? cache = null)
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
        var pictures = await PictureAddressesAsync(database, apiVersion, subjects, cache, ct);
        var running = await CompanionsRunningAsync(database, subjects, clock.UtcNow, ct);

        var roster = people.Here
            .Select(person => Describe(
                person.UserId,
                person.DisplayName,
                flagged,
                members,
                ranks.GetValueOrDefault(person.UserId),
                eighteenPlus.TryGetValue(person.UserId, out var marked) ? marked : null,
                pictures.GetValueOrDefault(person.UserId),
                running.Contains(person.UserId)))
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
            .Select(e => new { e.Id, e.Type, e.OccurredAt, e.Data })
            .ToListAsync(ct);

        // Read even with no facts on record: a Nuisance rank or a flag on a linked Discord account
        // still makes somebody Flagged.
        var match = (await FlagRules.ReadAsync(
                database, [subjectId], new Dictionary<string, TrustRank?> { [subjectId] = trustRank }, clock.UtcNow, ct))
            .GetValueOrDefault(subjectId) ?? FlagMatch.None;

        // Asked the way the roster asks, so the card and the row it opened from never disagree.
        var belonging = await MembersAndStaff.ReadAsync(database, [subjectId], ct);
        var standing = Standing(match, belonging.IsMember(subjectId), belonging.IsStaff(subjectId));

        var watched = match.Watch is not null;
        var companionRunning = (await CompanionsRunningAsync(database, [subjectId], clock.UtcNow, ct)).Contains(subjectId);

        if (facts.Count == 0)
        {
            // Nothing on record is a perfectly good answer, and saying so is better than a 404 the
            // overlay would have to translate. A watch can stand on somebody with no facts at all.
            return Results.Ok(new UserSummaryDto(
                subjectId, null, standing, match.PriorActions, null,
                match.Reasons, [], trustRank, Watched: watched, Invitation: belonging.Invitation(subjectId),
                CompanionRunning: companionRunning));
        }

        // The web profile's own figures, from the same query: times seen and first seen. Read only
        // when there are facts, because being seen in an instance is a fact.
        var seen = await new PresenceCounts(database).ForPersonAsync(subjectId, ct);
        var notes = NoteService.StandingCount(facts.Select(f => (f.Id, f.Type, f.Data)));
        var lastAction = facts.LastOrDefault(f => ActionNames.ContainsKey(f.Type));

        var joinedAt = facts.FirstOrDefault(f => f.Type == FactType.MemberJoined)?.OccurredAt;
        var displayName = facts.LastOrDefault(f => ReadDisplayName(f.Data) is not null) is { } named
            ? ReadDisplayName(named.Data)
            : null;

        // Only for somebody who may mark it, so the card offers Mark avatar to nobody else.
        var avatar = ModbotAuth.Allows(authentication.OwnerPermissions, ModbotPermissions.ManageSettings)
            ? await ProblemAvatarEndpoints.PersonAvatarAsync(database, subjectId, ct)
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
            trustRank,
            seen.Arrivals > 0 ? seen.Arrivals : null,
            seen.FirstSeenAt,
            notes > 0 ? notes : null,
            lastAction is null ? null : ActionNames[lastAction.Type],
            lastAction?.OccurredAt,
            watched,
            avatar?.AvatarName,
            avatar is null ? null : avatar.Listed,
            belonging.Invitation(subjectId),
            companionRunning));
    }

    /// <summary>
    /// What the card calls the things this group does to somebody, by the fact that records each: the
    /// kicks and bans the prior-actions count is made of, a lifted ban, and a warning.
    /// </summary>
    private static readonly Dictionary<string, string> ActionNames = new(StringComparer.Ordinal)
    {
        [FactType.MemberKicked] = "Kick",
        [FactType.GroupInstanceKick] = "Kick",
        [FactType.MemberBanned] = "Ban",
        [FactType.MemberUnbanned] = "Unban",
        [FactType.GroupInstanceWarn] = "Warn",
    };

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
    /// Which of these VRChat users have their companion running: a Modbot account linked to them
    /// (<see cref="ModbotUser.VRChatUserId"/>), enabled, with a device that is not revoked and was
    /// heard from within <see cref="CompanionRunningWindow"/>. One query for everybody. A device
    /// heard from that recently is far inside <see cref="DeviceStanding.IdleLimit"/>, so idleness
    /// needs no separate check.
    /// </summary>
    internal static async Task<HashSet<string>> CompanionsRunningAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var heardSince = now - CompanionRunningWindow;

        var running = await database.CompanionDevices
            .AsNoTracking()
            .Where(d => d.RevokedAt == null && d.LastSeenAt >= heardSince)
            .Join(
                database.Users.Where(u => !u.IsDisabled && u.VRChatUserId != null && subjectIds.Contains(u.VRChatUserId)),
                d => d.IssuedToUserId,
                u => u.Id,
                (d, u) => u.VRChatUserId!)
            .Distinct()
            .ToListAsync(ct);

        return running.ToHashSet(StringComparer.Ordinal);
    }

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

    /// <summary>
    /// The path on this server where each of these people's picture is served to a paired device,
    /// for everybody whose stored picture this server's cache already holds. Empty while the operator
    /// has VRChat pictures switched off. One query and a look in the cache, and no VRChat call.
    /// </summary>
    /// <remarks>
    /// <para>The address on the row names a version of the picture and never changes for it, so a
    /// short mark made from it goes on the end of the path. A person who changes their picture gets
    /// a different path, and the companion, which keeps pictures by path, fetches the new one rather
    /// than showing the old one until it restarts. The mark is not a secret and the route ignores it.</para>
    /// <para>Only somebody whose stored address is one VRChat serves pictures from
    /// (<see cref="VRChatFiles.IsPictureAddress(string?)"/>) and whose picture the cache holds is listed,
    /// so the route is not asked for what it would answer 404. The cache holds a picture once somebody
    /// has had it shown in the web app; a person whose picture has not been is listed from the
    /// roster read after it has.</para>
    /// </remarks>
    internal static async Task<Dictionary<string, string>> PictureAddressesAsync(
        ModbotContext database,
        int apiVersion,
        IReadOnlyCollection<string> subjectIds,
        VRChatFileCache? cache,
        CancellationToken ct)
    {
        var settings = await database.GetSettingsAsync(ct);
        if (!settings.VRChatImagesProxied || cache is null)
            return [];

        var rows = await database.VRChatUsers
            .AsNoTracking()
            .Where(u => subjectIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.ProfilePictureUrl, u.IconUrl, u.CurrentAvatarThumbnailImageUrl })
            .ToListAsync(ct);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var address = ProfilePictures.Best(row.ProfilePictureUrl, row.IconUrl, row.CurrentAvatarThumbnailImageUrl);
            if (!VRChatFiles.IsPictureAddress(address))
                continue;

            // Held, and not merely stored on the profile: the route sends nothing else.
            if (cache.Find(address!) is not { } held)
                continue;

            held.Content.Dispose();

            found[row.UserId] = PictureRoute(apiVersion, row.UserId, address!);
        }

        return found;
    }

    /// <summary>
    /// The path a person's picture is served at to a paired device, with the short mark that
    /// changes when the stored picture does.
    /// </summary>
    internal static string PictureRoute(int apiVersion, string subjectId, string storedAddress)
    {
        var mark = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storedAddress)))[..8].ToLowerInvariant();
        return $"/api/v{apiVersion}/companion/picture/{Uri.EscapeDataString(subjectId)}?v={mark}";
    }

    /// <summary>
    /// One person's stored picture, sent to a paired device from this server's cache: the same bytes,
    /// type and ETag the web app is sent for that person's face.
    /// </summary>
    /// <remarks>
    /// <para>The device names a person and never an address, so this can only ever send the picture
    /// stored for that person. It answers 404 for somebody with no stored picture, while the operator
    /// has VRChat pictures switched off, and for a picture the cache does not hold, and it never
    /// sends the device on to VRChat.</para>
    /// <para><strong>It sends what the cache holds and fetches nothing.</strong> It has no VRChat gate,
    /// so a moderator glancing at a roster cannot cause any request to VRChat, of any kind. The cache
    /// is filled by the web app's own picture route (<see cref="VRChatFileEndpoints"/>) when somebody
    /// has a face shown there.</para>
    /// </remarks>
    public static async Task<IResult> PictureAsync(
        int apiVersion,
        string subjectId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        [FromServices] VRChatFileCache cache,
        ModbotContext database,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (subjectId is not { Length: > 0 })
            return CompanionApiErrors.Malformed("A subjectId is required.");

        var settings = await database.GetSettingsAsync(ct);
        if (!settings.VRChatImagesProxied)
            return Results.NotFound();

        var row = await database.VRChatUsers
            .AsNoTracking()
            .Where(u => u.UserId == subjectId)
            .Select(u => new { u.ProfilePictureUrl, u.IconUrl, u.CurrentAvatarThumbnailImageUrl })
            .FirstOrDefaultAsync(ct);

        var address = row is null
            ? null
            : ProfilePictures.Best(row.ProfilePictureUrl, row.IconUrl, row.CurrentAvatarThumbnailImageUrl);

        if (!VRChatFiles.IsPictureAddress(address))
            return Results.NotFound();

        return VRChatFileEndpoints.ServeCached(context, address!, cache) ?? Results.NotFound();
    }

    /// <param name="flagged">What <see cref="FlagRules"/> decided for everybody being described.</param>
    /// <param name="belonging">What <see cref="MembersAndStaff"/> read for everybody being described.</param>
    /// <param name="pictureUrl">The path this person's picture is served at, or null (<see cref="RosterMemberDto"/>).</param>
    /// <param name="companionRunning">Whether their companion is running now, or null where nobody asked (<see cref="RosterMemberDto"/>).</param>
    internal static RosterMemberDto Describe(
        string subjectId,
        string? displayName,
        IReadOnlyDictionary<string, FlagMatch> flagged,
        MembersAndStaff belonging,
        TrustRank? trustRank = null,
        bool? eighteenPlus = null,
        string? pictureUrl = null,
        bool? companionRunning = null)
    {
        var match = flagged.GetValueOrDefault(subjectId) ?? FlagMatch.None;

        return new RosterMemberDto(
            subjectId,
            displayName,
            Standing(match, belonging.IsMember(subjectId), belonging.IsStaff(subjectId)),
            match.PriorActions,
            match.Reasons,
            trustRank,
            eighteenPlus,
            pictureUrl,
            belonging.IsMember(subjectId),
            belonging.Invitation(subjectId),
            companionRunning);
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
