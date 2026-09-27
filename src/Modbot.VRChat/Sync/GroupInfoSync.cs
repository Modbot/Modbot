using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Watches the managed group's own metadata and records what changes.
/// </summary>
/// <remarks>
/// <para>
/// One request per pass. <c>GetGroup(includeRoles: true)</c> returns the member counts, the name,
/// the description and every role definition together, so the role list — which is what makes a
/// <c>RoleGranted</c> fact readable, since VRChat's audit entry names a role by id — costs nothing
/// beyond the poll that was happening anyway.
/// </para>
/// <para>
/// <strong>A fact is written only when something actually changed.</strong> Writing one per poll
/// would be 288 identical rows a day asserting that nothing happened, and it would corrupt every
/// question of the form "how often does this change" — the same failure as the avatar lines in the
/// log research (§4.0), where 82% of what looked like changes were restatements of what was
/// already true. So the last recorded state is kept, and the poll compares against it.
/// </para>
/// <para>
/// The first pass is the exception: it writes a baseline fact carrying the whole snapshot, because
/// there is nothing to compare against and because the member count it records is what the
/// <c>members.net</c> series is counted forward from.
/// </para>
/// </remarks>
public sealed class GroupInfoSync
{
    private readonly IVRChatGate _gate;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public GroupInfoSync(
        IVRChatGate gate,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ModbotContext db,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _facts = facts;
        _partitions = partitions;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<GroupInfoRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new GroupInfoRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var groupId = settings.ManagedGroupId;

        // `groups.read` already has a budget (spec 4.2: one request per 10 seconds, shared with
        // group roles). Resource-scoped on the group, because VRChat's limits are sometimes
        // per-resource (spec 4.3.1) and the model must keep that dimension even where a
        // single-group appliance collapses it to one.
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.GroupsRead, groupId, "GetGroup");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupWithHttpInfoAsync(
                groupId, includeRoles: true, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return await FailedAsync(settings, result, ct).ConfigureAwait(false);

        if (result.Value is not { } group)
        {
            return await RecordPollAsync(
                settings,
                new GroupInfoRunResult(SyncOutcome.Failed, Message: "VRChat returned no group body"),
                ct).ConfigureAwait(false);
        }

        // The pictures, kept beside the snapshot rather than in it. They change on their own
        // schedule and are not worth a fact, but the public instances report needs them: without an
        // icon and a banner a group is a grey box on modbot.co.
        RecordPictures(settings, group.IconUrl, group.BannerUrl);

        // The languages and links, beside the pictures and for the same reason: the VRChat
        // analytics page shows the group as it is now, and neither is worth a fact.
        RecordLanguagesAndLinks(settings, group.Languages, group.Links);

        // The galleries, for the Gallery tab to list without a request of its own.
        RecordGalleries(settings, group.Galleries);

        // Modbot's own roles and permissions in the group, from `myMember` in the same answer, so a
        // refusal for a missing permission can say what the account has (VRChatGroupPermissions).
        RecordAccount(settings, result.RawResponse);

        // The two counts, every poll, whether or not anything changed. This is a different store
        // from the facts below with a different question behind it: the My Group chart shows the
        // readings themselves, and a reading that said the same thing as the last one is still a
        // point on it at a time of its own. Saved with the poll time by RecordPollAsync.
        _db.GroupMemberCounts.Add(new GroupMemberCount
        {
            GroupId = groupId,
            CountedAt = _clock.UtcNow,
            MemberCount = group.MemberCount,
            OnlineMemberCount = group.OnlineMemberCount,
        });

        var current = GroupInfoSnapshot.From(group);
        var previous = GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot);
        var lastSeen = settings.GroupInfoPolledAt;

        if (previous is null)
        {
            await WriteAsync(groupId, current.BaselinePayload(), since: null, ct).ConfigureAwait(false);
            settings.GroupInfoSnapshot = current.ToJson();

            _log.Information(
                "Recorded the group's baseline: {MemberCount} members, {RoleCount} roles",
                current.MemberCount, current.Roles.Count);

            return await RecordPollAsync(
                settings,
                new GroupInfoRunResult(SyncOutcome.Produced, Baseline: true),
                ct).ConfigureAwait(false);
        }

        var changed = current.DifferencesFrom(previous);

        if (changed.Count == 0)
        {
            // The common case, and it writes nothing. Only the poll time is recorded, so the UI
            // can say the answer it is showing is fresh (spec 4.2.3) without a fact having to
            // exist to prove it.
            return await RecordPollAsync(settings, new GroupInfoRunResult(SyncOutcome.Quiet), ct)
                .ConfigureAwait(false);
        }

        await WriteAsync(groupId, current.ChangePayload(previous, changed), lastSeen, ct).ConfigureAwait(false);
        settings.GroupInfoSnapshot = current.ToJson();

        _log.Information("Group metadata changed: {Fields}", string.Join(", ", changed));

        return await RecordPollAsync(
            settings,
            new GroupInfoRunResult(SyncOutcome.Produced, changed),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the group as VRChat answered a profile edit made from Modbot, so the page shows the
    /// new values at once rather than after the next poll. Saved by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same checks as a poll: pictures only when <c>https</c>, links only when <c>http</c> or
    /// <c>https</c>. The snapshot takes only the fields an edit changes
    /// (<see cref="GroupInfoSnapshot.WithEdit"/>).
    /// </para>
    /// <para>
    /// Because the snapshot now holds the new values, the next poll sees no change and writes no
    /// <see cref="FactType.GroupInfoChanged"/> for it. That is intended: the edit already wrote
    /// its own fact naming who made it, which a poll never can, and the group's audit log records
    /// VRChat's side. Before the first poll there is no snapshot, and none is made here — the
    /// first one is the poll's baseline.
    /// </para>
    /// </remarks>
    public static void RecordEdit(Settings settings, global::VRChat.API.Model.Group group)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(group);

        RecordPictures(settings, group.IconUrl, group.BannerUrl);
        RecordLanguagesAndLinks(settings, group.Languages, group.Links);

        if (!string.IsNullOrWhiteSpace(group.Name))
            settings.ManagedGroupName = group.Name;

        if (GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot) is { } previous)
            settings.GroupInfoSnapshot = previous.WithEdit(group).ToJson();
    }

    /// <summary>
    /// Stores the roles as VRChat answered a role edit made from Modbot: the whole list after a
    /// change or a delete, the one new role after a create. Saved by the caller.
    /// </summary>
    /// <remarks>
    /// For the reason <see cref="RecordEdit"/> gives: the next poll then sees no change and writes
    /// no <see cref="FactType.GroupInfoChanged"/> for one the edit already recorded with who made
    /// it. Before the first poll there is no snapshot, and none is made here.
    /// </remarks>
    public static void RecordRoles(Settings settings, IReadOnlyList<global::VRChat.API.Model.GroupRole>? all = null, global::VRChat.API.Model.GroupRole? added = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot) is not { } previous)
            return;

        if (all is not null)
            previous = previous.WithRoles(all);
        else if (added is not null)
            previous = previous.WithRole(added);
        else
            return;

        settings.GroupInfoSnapshot = previous.ToJson();
    }

    /// <summary>
    /// Keeps the group's icon and banner up to date, ignoring anything that is not an
    /// <c>https</c> address.
    /// </summary>
    /// <remarks>
    /// The check is here, where VRChat's answer first lands, so that nothing else downstream has
    /// to wonder whether a stored picture address is safe to put in an <c>img src</c>.
    /// </remarks>
    private static void RecordPictures(Settings settings, string? icon, string? banner)
    {
        settings.ManagedGroupIconUrl = Picture(icon) ?? settings.ManagedGroupIconUrl;
        settings.ManagedGroupBannerUrl = Picture(banner) ?? settings.ManagedGroupBannerUrl;

        static string? Picture(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
                ? parsed.AbsoluteUri
                : null;
    }

    /// <summary>
    /// Keeps the group's languages and links up to date. A list VRChat left out entirely keeps
    /// what was recorded before; an empty one means the group has none, and is recorded as such.
    /// </summary>
    /// <remarks>
    /// A link that is not an absolute <c>http</c> or <c>https</c> address is dropped here, for the
    /// same reason as a picture: whatever is stored goes straight into an <c>href</c>. A language
    /// code is kept as VRChat wrote it, less blanks and repeats; the page turns it into a name.
    /// </remarks>
    private static void RecordLanguagesAndLinks(
        Settings settings,
        IReadOnlyList<string>? languages,
        IReadOnlyList<string>? links)
    {
        if (languages is not null)
        {
            var kept = languages
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (settings.ManagedGroupLanguages is null || !settings.ManagedGroupLanguages.SequenceEqual(kept, StringComparer.Ordinal))
                settings.ManagedGroupLanguages = kept;
        }

        if (links is not null)
        {
            var kept = links
                .Select(Link)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (settings.ManagedGroupLinks is null || !settings.ManagedGroupLinks.SequenceEqual(kept, StringComparer.Ordinal))
                settings.ManagedGroupLinks = kept;
        }

        static string? Link(string? url) =>
            Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
                ? parsed.AbsoluteUri
                : null;
    }

    /// <summary>
    /// Keeps the group's galleries up to date. An answer with no gallery list keeps what was
    /// recorded before; an empty one means the group has none.
    /// </summary>
    internal static void RecordGalleries(Settings settings, IReadOnlyList<global::VRChat.API.Model.GroupGallery>? galleries)
    {
        if (galleries is null)
            return;

        var json = GroupGallerySnapshot.ToJson(GroupGallerySnapshot.From(galleries));

        if (!string.Equals(settings.ManagedGroupGalleries, json, StringComparison.Ordinal))
            settings.ManagedGroupGalleries = json;
    }

    /// <summary>
    /// Keeps Modbot's own role ids and permissions in the group up to date. An answer with no
    /// <c>myMember</c> keeps what was recorded before.
    /// </summary>
    internal static void RecordAccount(Settings settings, string? groupJson)
    {
        var (roleIds, permissions) = VRChatGroupPermissions.AccountFrom(groupJson);

        if (roleIds is not null
            && (settings.VRChatAccountRoleIds is null || !settings.VRChatAccountRoleIds.SequenceEqual(roleIds, StringComparer.Ordinal)))
        {
            settings.VRChatAccountRoleIds = roleIds;
        }

        if (permissions is not null
            && (settings.VRChatAccountPermissions is null || !settings.VRChatAccountPermissions.SequenceEqual(permissions, StringComparer.Ordinal)))
        {
            settings.VRChatAccountPermissions = permissions;
        }
    }

    private async Task<GroupInfoRunResult> FailedAsync<T>(
        Settings settings,
        VRChatResult<T> result,
        CancellationToken ct)
    {
        if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Never retried, and not logged as an error: a cold stop is the design working
            // (spec 4.3.1). The audit log runs in its own bucket and is unaffected.
            _log.Information(
                "Group-info sync is paused: {Reason}",
                result.ErrorMessage ?? "the groups.read bucket is cold-stopped");

            return await RecordPollAsync(
                settings,
                new GroupInfoRunResult(SyncOutcome.RateLimited, Message: result.ErrorMessage),
                ct).ConfigureAwait(false);
        }

        _log.Warning(
            "Group-info sync could not read the group: {Status} {Reason}",
            result.StatusCode,
            result.ErrorMessage ?? "no detail");

        return await RecordPollAsync(
            settings,
            new GroupInfoRunResult(SyncOutcome.Failed, Message: result.ErrorMessage),
            ct).ConfigureAwait(false);
    }

    private async Task<GroupInfoRunResult> RecordPollAsync(
        Settings settings,
        GroupInfoRunResult result,
        CancellationToken ct)
    {
        settings.GroupInfoPolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result;
    }

    /// <param name="since">
    /// When the group was last seen unchanged, or null when this is the first observation.
    /// </param>
    private async Task WriteAsync(
        string groupId,
        System.Text.Json.Nodes.JsonObject data,
        DateTimeOffset? since,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var fact = new FactRecord
        {
            Type = FactType.GroupInfoChanged,

            // A poll knows only that the change happened between the previous poll and this one.
            // Spec 5.3 is explicit that collapsing such a window into an instant invents precision
            // and produces fake spikes in fine-grained charts, so the window is recorded. A
            // baseline has no window: it is not a change, it is the first look.
            OccurredAt = since is { } from && from < now ? from : now,
            OccurredBefore = since is { } lower && lower < now ? now : null,

            SubjectPlatform = FactPlatform.VRChat,

            // The group is the subject. It is the only fact type for which that is true; every
            // other subject is a person. The id is carried through untouched (spec 3.1.1).
            SubjectId = groupId,

            // No actor. VRChat's group object says what the group looks like, never who changed
            // it — that attribution lives in the audit log, and inventing one here would be
            // exactly the false accountability spec 5.8 depends on not having.
            ActorPlatform = null,
            ActorId = null,

            Source = FactSource.SyncDiff,
            Data = data,
        };

        await _partitions.EnsureForAsync(fact.OccurredAt, ct).ConfigureAwait(false);
        await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
    }
}
