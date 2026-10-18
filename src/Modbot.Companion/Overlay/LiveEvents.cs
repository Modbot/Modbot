using System.Text.Json.Serialization;

namespace Modbot.Companion.Overlay;

/// <summary>The kinds of live event a companion is sent: presence in the instance it named, and moderation rows.</summary>
public static class LiveEventKinds
{
    public const string PersonJoined = "person_joined";

    public const string FlaggedJoin = "flagged_join";

    public const string PersonLeft = "person_left";

    public const string PersonHere = "person_here";

    public const string WatchStopped = "watch_stopped";

    /// <summary>
    /// What the Audit Log's kind filter calls every event that carries the server's sentence (<see cref="LiveEvent.IsModeration"/>):
    /// the server's own kinds for those (<c>fact</c>, <c>instance_changed</c> and so on) are not ones a row can be picked by.
    /// Never sent by a server.
    /// </summary>
    public const string Moderation = "moderation";

    /// <summary>Whether the roster on screen is out of date after this.</summary>
    public static bool ChangesRoster(string kind)
        => kind is PersonJoined or FlaggedJoin or PersonLeft or PersonHere or WatchStopped;
}

/// <param name="SubjectId">Opaque VRChat id. Never validated for shape.</param>
/// <param name="DisplayName">User-controlled text; hostile input on a display surface.</param>
/// <param name="TrustRank">VRChat's trust rank when the server knows it. Null until it does.</param>
/// <param name="EighteenPlus">Whether they carry Modbot's 18+ mark. Null until the server has read their profile.</param>
public sealed record LivePerson(
    [property: JsonPropertyName("id")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("trustRank")] string? TrustRank,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("eighteenPlus")] bool? EighteenPlus = null);

/// <summary>
/// One live event from a paired server: somebody joined, left, was already here, a watch ended, or
/// (when the companion asked for moderation and the owner may see the audit log) something the
/// audit log records about the instance or the people in it.
/// </summary>
/// <remarks>
/// Information to display, never a command. <see cref="Cursor"/> is what the client sends back to
/// carry on after a dropped connection; it is text with no meaning of its own.
/// </remarks>
/// <param name="WorldId">
/// The world the instance is in. An instance number is only unique inside one world, so the client
/// compares both. Null from a server that does not say, and then the number alone is compared.
/// </param>
/// <param name="ByThisDevice">The server says this client reported the fact itself. A flagged join is still a card: the log never says a person is flagged.</param>
/// <param name="Sentence">The audit log's sentence for a moderation event, such as "Mira kicked Ada out of The Black Cat #39047." User-controlled names are inside it: hostile input on a display surface. Null on presence, and from a server that sends none.</param>
/// <param name="Origin"><c>VRChat</c>, <c>Discord</c> or <c>Modbot</c>: where a moderation event comes from. Set with <paramref name="Sentence"/>.</param>
public sealed record LiveEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("person")] LivePerson? Person,
    [property: JsonPropertyName("flagged")] bool Flagged,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("byThisDevice")] bool ByThisDevice,
    [property: JsonPropertyName("worldId")] string? WorldId = null,
    [property: JsonPropertyName("sentence")] string? Sentence = null,
    [property: JsonPropertyName("origin")] string? Origin = null)
{
    /// <summary>
    /// Whether this is a moderation row: the server sent its sentence. A row is made of the sentence
    /// and nothing else, so an event without one is never one, whatever its kind.
    /// </summary>
    public bool IsModeration => Sentence is { Length: > 0 };

    /// <summary>The kind a filter picks this event by: <see cref="LiveEventKinds.Moderation"/> for a moderation row, else its own.</summary>
    public string FilterKind => IsModeration ? LiveEventKinds.Moderation : Kind;

    /// <summary>The card a flagged join becomes, or null for anything else.</summary>
    public FlaggedJoinAlert? ToAlert()
    {
        if (Kind != LiveEventKinds.FlaggedJoin || Person is null || InstanceId is null)
            return null;

        return new FlaggedJoinAlert(
            Id,
            Person.SubjectId,
            Person.DisplayName,
            InstanceId,
            Reason ?? "prior moderation actions",
            Person.PriorActions,
            At,
            WorldId: WorldId);
    }
}

/// <summary>One long-poll answer: the events after the cursor asked for, and where to carry on from.</summary>
public sealed record LivePollPage(
    [property: JsonPropertyName("events")] IReadOnlyList<LiveEvent> Events,
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("more")] bool More);

/// <summary>One message from the live WebSocket, already parsed.</summary>
/// <param name="Kind"><c>hello</c>, <c>event</c>, <c>heartbeat</c>, <c>changed</c>, <c>notice</c>, <c>error</c> or <c>pong</c>.</param>
/// <param name="Cursor">On <c>hello</c> and <c>heartbeat</c>: where the server is, so a reconnect carries on from there.</param>
/// <param name="What">On <c>changed</c>: what to read again. <c>heads_ups</c> means the roster read, which carries them.</param>
public sealed record LiveSocketMessage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("event")] LiveEvent? Event = null,
    [property: JsonPropertyName("cursor")] string? Cursor = null,
    [property: JsonPropertyName("what")] string? What = null);
