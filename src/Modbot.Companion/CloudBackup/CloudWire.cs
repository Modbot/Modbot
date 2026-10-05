using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.CloudBackup;

/// <summary>One event waiting to go to Modbot Cloud, as the Cloud Server page's Next batch card shows it.</summary>
/// <param name="EventId">The unique id the event goes under.</param>
/// <param name="Type">What happened.</param>
/// <param name="OccurredAt">When, on Cloud's clock as far as it has been measured.</param>
/// <param name="SubjectId">The VRChat user it is about. Always sent.</param>
/// <param name="DisplayName">Their name, when the log gave one. Always sent when there is one.</param>
/// <param name="WorldId">Null when it is left out of this event.</param>
/// <param name="InstanceId">Null when it is left out of this event.</param>
/// <param name="GroupId">Null when it is left out, or when the instance has no group.</param>
/// <param name="AvatarName">Null when it is left out, or when the event is not an avatar change.</param>
/// <param name="InGroup">True for an instance a group owns.</param>
public sealed record CloudQueuedEvent(
    string EventId,
    CompanionEventType Type,
    DateTimeOffset OccurredAt,
    string SubjectId,
    string? DisplayName,
    string? WorldId,
    string? InstanceId,
    string? GroupId,
    string? AvatarName,
    bool InGroup)
{
    /// <summary>Which of the five kinds this is, or none for a kind the backup never sends.</summary>
    public CloudEventKinds Kind => Type switch
    {
        CompanionEventType.InstanceJoined => CloudEventKinds.Joined,
        CompanionEventType.InstancePresenceObserved => CloudEventKinds.AlreadyHere,
        CompanionEventType.InstanceLeft => CloudEventKinds.Left,
        CompanionEventType.AvatarChanged => CloudEventKinds.AvatarChanged,
        CompanionEventType.LogStopped => CloudEventKinds.StoppedLogging,
        _ => CloudEventKinds.None,
    };
}

/// <summary>What the Next batch card shows: the events waiting and the offset to Cloud's clock they will carry.</summary>
public sealed record CloudWaiting(IReadOnlyList<CloudQueuedEvent> Events, TimeSpan ClockOffset)
{
    public static CloudWaiting None { get; } = new([], TimeSpan.Zero);
}

/// <summary>
/// The shape of an event as it is kept in the outbox and sent to Modbot Cloud: the paired server's
/// event with the details the person chose not to send taken out.
/// </summary>
/// <remarks>
/// <para><strong>Left out means left out.</strong> A detail whose box is off is not in the JSON
/// at all, not blank and not a placeholder, from the moment the event is queued. Nothing the person
/// chose to hold back is written to the outbox on this PC either.</para>
/// <para><strong>A Cloud that has not been updated</strong> refuses a whole batch for one event with
/// no world id or no instance id, and the client then deletes it. <see cref="FillMissing"/> puts the
/// word <see cref="Hidden"/> in their place at send time, unless Cloud's clock answer said it takes
/// them missing.</para>
/// </remarks>
public static class CloudWire
{
    /// <summary>What is sent in place of a world id or instance id that was left out, for a Cloud that needs one.</summary>
    public const string Hidden = "hidden";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The event as JSON, with every detail whose box is off for this kind of instance taken out.</summary>
    public static string Shape(CompanionEvent companionEvent, CloudSection section, bool inGroup)
    {
        ArgumentNullException.ThrowIfNull(companionEvent);
        ArgumentNullException.ThrowIfNull(section);

        var node = JsonSerializer.SerializeToNode(companionEvent, Json)!.AsObject();

        if (!section.WorldId)
            node.Remove("worldId");

        if (!section.InstanceId)
            node.Remove("instanceId");

        // A non-group event keeps the group it does not have as null, as the backup always has.
        if (inGroup && !section.GroupId)
            node.Remove("groupId");

        if (!section.AvatarName && node["data"] is JsonObject data)
            data.Remove("avatarName");

        return node.ToJsonString();
    }

    /// <summary>
    /// The same event with <see cref="Hidden"/> where a world id or instance id is missing or blank.
    /// Anything that is not a JSON object comes back as it was.
    /// </summary>
    public static string FillMissing(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject node)
            return json;

        var changed = false;
        foreach (var field in new[] { "worldId", "instanceId" })
        {
            if (IsMissing(node[field]))
            {
                node[field] = Hidden;
                changed = true;
            }
        }

        return changed ? node.ToJsonString() : json;
    }

    /// <summary>
    /// Reads an event back as a <see cref="CompanionEvent"/> even when its world or instance was
    /// left out, which is how the Events screen is told the backup took it. Null when it cannot be read.
    /// </summary>
    public static CompanionEvent? ReadBack(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
                return null;

            node["worldId"] ??= string.Empty;
            node["instanceId"] ??= string.Empty;
            if (!node.ContainsKey("groupId"))
                node["groupId"] = null;

            return node.Deserialize<CompanionEvent>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One waiting event as the card shows it, or null for a line that is not an event this client wrote.</summary>
    public static CloudQueuedEvent? Queued(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node
                || !Enum.TryParse<CompanionEventType>(Text(node, "type"), ignoreCase: true, out var type)
                || Text(node, "companionEventId") is not { } id
                || Text(node, "subjectId") is not { } subject
                || !DateTimeOffset.TryParse(
                    Text(node, "occurredAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                return null;
            }

            var data = node["data"] as JsonObject;

            // A group the person left out has no groupId key at all. An instance with no group has one,
            // and it is null.
            var inGroup = !(node.ContainsKey("groupId") && node["groupId"] is null);

            return new CloudQueuedEvent(
                id,
                type,
                at,
                subject,
                data is null ? null : Text(data, "displayName"),
                Text(node, "worldId"),
                Text(node, "instanceId"),
                Text(node, "groupId"),
                data is null ? null : Text(data, "avatarName"),
                inGroup);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsMissing(JsonNode? node)
        => node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text));

    private static string? Text(JsonObject node, string field)
        => node[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
