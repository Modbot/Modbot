using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// The kinds of event the live stream carries (live updates design §2), and which fact each one
/// comes from.
/// </summary>
/// <remarks>
/// Every live event is a fact under another name, so its id is the fact's id and a client resumes
/// with the same cursor the audit log and the event WebSocket use. A fact of any other type is on
/// the stream too, as <see cref="Fact"/>, carrying its type: that is what lets the audit log and
/// the member lists redraw as facts land. The named kinds are the ones a client does something
/// particular with.
/// </remarks>
public static class LiveKinds
{
    /// <summary>Any fact without a named kind. Its <c>type</c> says what it is.</summary>
    public const string Fact = "fact";

    public const string PersonJoined = "person_joined";

    /// <summary>A join by somebody this group has kicked or banned before. Sent instead of <see cref="PersonJoined"/>.</summary>
    public const string FlaggedJoin = "flagged_join";

    public const string PersonLeft = "person_left";

    /// <summary>Somebody who was already there when a moderator started watching.</summary>
    public const string PersonHere = "person_here";

    /// <summary>A moderator's client said VRChat's log stopped: their watch of the instance ended.</summary>
    public const string WatchStopped = "watch_stopped";

    public const string InstanceOpened = "instance_opened";

    public const string InstanceClosed = "instance_closed";

    public const string InstanceChanged = "instance_changed";

    /// <summary>Unusual activity, from the alert watchers (AI insights design §8).</summary>
    public const string Alert = "alert";

    public const string ReviewOpened = "review_opened";

    public const string ReviewClosed = "review_closed";

    public static string? Of(string factType) => factType switch
    {
        FactType.InstanceJoined => PersonJoined,
        FactType.InstanceLeft => PersonLeft,
        FactType.InstancePresenceObserved => PersonHere,
        FactType.InstanceLogStopped => WatchStopped,
        FactType.GroupInstanceCreated => InstanceOpened,
        FactType.GroupInstanceClosed => InstanceClosed,
        FactType.GroupInstanceUpdated => InstanceChanged,
        FactType.InsightAlert => Alert,
        FactType.ReviewOpened => ReviewOpened,
        FactType.ReviewClosed => ReviewClosed,
        _ => null,
    };

    /// <summary>Where somebody is standing right now: needs "See live instances", like the Live page.</summary>
    public static bool IsPresence(string kind)
        => kind is PersonJoined or FlaggedJoin or PersonLeft or PersonHere or WatchStopped;

    public static bool IsInstance(string kind)
        => kind is InstanceOpened or InstanceClosed or InstanceChanged;

    public static bool IsReview(string kind)
        => kind is ReviewOpened or ReviewClosed;
}

/// <param name="Id">Opaque VRChat id. Never parsed, never validated (foundation §3.1.1).</param>
/// <param name="TrustRank">
/// VRChat's trust rank, when Modbot knows it. Null until the trust rank sync lands; the field is in
/// the shape now so clients do not change again when it does.
/// </param>
/// <param name="Standing"><c>Flagged</c>, <c>Staff</c>, <c>Member</c> or <c>Ordinary</c>, as the roster says it.</param>
public sealed record LivePerson(
    string Id,
    string? DisplayName,
    string? TrustRank,
    string Standing,
    int PriorActions,
    IReadOnlyList<string> Flags);

/// <param name="Platform"><c>VRChat</c>, <c>Discord</c> or <c>Modbot</c>.</param>
/// <param name="Id">Opaque. Never parsed, never validated (foundation §3.1.1).</param>
/// <param name="Kind"><c>Person</c>, <c>Instance</c>, <c>Group</c>, <c>Role</c>, <c>Account</c> or <c>Other</c>.</param>
public sealed record LiveSubject(string Platform, string Id, string Kind);

/// <param name="Name">The name recorded in the fact at the time, or null. Never looked up now.</param>
public sealed record LiveActor(string Platform, string Id, string? Name);

/// <summary>
/// One live event, as the Live page and the companion receive it (live updates design §2). Version 1.
/// </summary>
/// <param name="Id">The fact's id, as text.</param>
/// <param name="Cursor">What a client stores and sends back to carry on. Equal to <paramref name="Id"/> today; documented as opaque.</param>
/// <param name="Type">The fact's type, such as <c>vrchat.group.member.ban</c>. What a page matches on.</param>
/// <param name="TypeRaw">For an event VRChat reported that Modbot has no name for: the word VRChat used.</param>
/// <param name="Category"><c>moderation</c> or <c>operational</c>: which log it belongs to.</param>
/// <param name="Label">A short English label for the type.</param>
/// <param name="Source">Where Modbot learned of it: <c>AuditLog</c>, <c>SyncDiff</c>, <c>Client</c>, <c>Discord</c>, <c>Manual</c> or <c>Modbot</c>.</param>
/// <param name="At">When it happened.</param>
/// <param name="OccurredBefore">Null when the time is exact; otherwise it happened between <paramref name="At"/> and this.</param>
/// <param name="ObservedAt">When Modbot learned of it.</param>
/// <param name="Subject">Who or what it happened to.</param>
/// <param name="Actor">Who did it, or null when nobody did.</param>
/// <param name="InstanceId">VRChat's number for the instance it happened in, when the fact names one.</param>
/// <param name="WorldId">The world it happened in, when the fact names one.</param>
/// <param name="WorldName">
/// What that world is called, from <c>vrchat_world</c> as it stands now. Null when Modbot has only
/// ever seen the id, so a client shows the id instead. Never fetched from VRChat for this.
/// </param>
/// <param name="Person">Who it is about, described as the roster describes people, for the presence kinds. Null otherwise.</param>
/// <param name="Flagged">True on a <see cref="LiveKinds.FlaggedJoin"/>.</param>
/// <param name="Reason">Why the join was flagged, in words, or null.</param>
/// <param name="ByThisDevice">
/// For a companion connection: whether this device reported the fact itself. The overlay does not
/// raise a card for a join its own log just showed it. Always false on the web.
/// </param>
/// <param name="Data">The fact's payload. Null on companion connections.</param>
public sealed record LiveEvent(
    string Id,
    string Cursor,
    string Kind,
    string Type,
    string? TypeRaw,
    string Category,
    string Label,
    string Source,
    DateTimeOffset At,
    DateTimeOffset? OccurredBefore,
    DateTimeOffset ObservedAt,
    LiveSubject Subject,
    LiveActor? Actor,
    string? InstanceId,
    string? WorldId,
    string? WorldName,
    LivePerson? Person,
    bool Flagged,
    string? Reason,
    bool ByThisDevice,
    JsonNode? Data)
{
    /// <summary>The fact id, for the cursor arithmetic.</summary>
    public long FactId => long.Parse(Id, NumberStyles.None, CultureInfo.InvariantCulture);
}

/// <summary>The messages the live WebSocket sends, and the shape of a long-poll answer.</summary>
public sealed record LiveHello(string Kind, int Version, int HeartbeatSeconds, string Cursor);

public sealed record LiveEventMessage(string Kind, LiveEvent Event);

public sealed record LiveHeartbeat(string Kind, string Cursor);

public sealed record LiveNotice(string Kind, string Code, string Message);

public sealed record LiveError(string Kind, string Message);

public sealed record LivePong(string Kind);

/// <param name="Cursor">Send back as <c>after</c> to carry on. Moves past events this caller was not sent.</param>
/// <param name="More">Events are ready past these: ask again straight away.</param>
public sealed record LivePollResponse(
    IReadOnlyList<LiveEvent> Events,
    string Cursor,
    bool More,
    LiveNotice? Notice);

public static class LiveJson
{
    public const int Version = 1;

    /// <summary>camelCase, like the rest of the web API and the companion protocol.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static LiveNotice HistoryTrimmed { get; } =
        new("notice", "history_trimmed", "Events before the oldest kept fact are gone.");

    public static string Text(long cursor) => cursor.ToString(CultureInfo.InvariantCulture);
}
