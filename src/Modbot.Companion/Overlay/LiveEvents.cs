using System.Text.Json.Serialization;

namespace Modbot.Companion.Overlay;

/// <summary>The kinds of live event a companion is sent: presence in the instance it named.</summary>
public static class LiveEventKinds
{
    public const string PersonJoined = "person_joined";

    public const string FlaggedJoin = "flagged_join";

    public const string PersonLeft = "person_left";

    public const string PersonHere = "person_here";

    public const string WatchStopped = "watch_stopped";

    /// <summary>Whether the roster on screen is out of date after this.</summary>
    public static bool ChangesRoster(string kind)
        => kind is PersonJoined or FlaggedJoin or PersonLeft or PersonHere or WatchStopped;
}

/// <param name="SubjectId">Opaque VRChat id. Never validated for shape.</param>
/// <param name="DisplayName">User-controlled text; hostile input on a display surface.</param>
/// <param name="TrustRank">VRChat's trust rank when the server knows it. Null until it does.</param>
public sealed record LivePerson(
    [property: JsonPropertyName("id")] string SubjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("trustRank")] string? TrustRank,
    [property: JsonPropertyName("standing")] RosterStanding Standing,
    [property: JsonPropertyName("priorActions")] int PriorActions,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags);

/// <summary>
/// One live event from a paired server: somebody joined, left, was already here, or a watch ended.
/// </summary>
/// <remarks>
/// Information to display, never a command. <see cref="Cursor"/> is what the client sends back to
/// carry on after a dropped connection; it is text with no meaning of its own.
/// </remarks>
/// <param name="ByThisDevice">The server says this client reported the fact itself. No card for those.</param>
public sealed record LiveEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("person")] LivePerson? Person,
    [property: JsonPropertyName("flagged")] bool Flagged,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("byThisDevice")] bool ByThisDevice)
{
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
            At);
    }
}

/// <summary>One long-poll answer: the events after the cursor asked for, and where to carry on from.</summary>
public sealed record LivePollPage(
    [property: JsonPropertyName("events")] IReadOnlyList<LiveEvent> Events,
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("more")] bool More);

/// <summary>One message from the live WebSocket, already parsed.</summary>
/// <param name="Kind"><c>hello</c>, <c>event</c>, <c>heartbeat</c>, <c>notice</c>, <c>error</c> or <c>pong</c>.</param>
/// <param name="Cursor">On <c>hello</c> and <c>heartbeat</c>: where the server is, so a reconnect carries on from there.</param>
public sealed record LiveSocketMessage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("event")] LiveEvent? Event = null,
    [property: JsonPropertyName("cursor")] string? Cursor = null);
