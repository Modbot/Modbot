using System.Numerics;
using System.Text.Json.Nodes;
using Modbot.Companion.Instances;

namespace Modbot.Companion.CloudBackup;

/// <summary>The five kinds of event the Modbot Cloud backup can send, each of which can be switched off.</summary>
[Flags]
public enum CloudEventKinds
{
    None = 0,
    Joined = 1,
    AlreadyHere = 2,
    Left = 4,
    AvatarChanged = 8,

    /// <summary>"Modbot stopped logging": VRChat's log stopped while the moderator was in the instance.</summary>
    StoppedLogging = 16,

    All = Joined | AlreadyHere | Left | AvatarChanged | StoppedLogging,
}

/// <summary>The details of an event that can be left out of what the backup sends.</summary>
public enum CloudDetail
{
    WorldId,
    InstanceId,

    /// <summary>Only for an instance a group owns; a non-group instance has no group to leave out.</summary>
    GroupId,

    AvatarName,
}

/// <summary>The names the five kinds go by: in <c>settings.json</c> and on screen.</summary>
public static class CloudEventKindNames
{
    /// <summary>Every kind, in the order the screen lists them.</summary>
    public static IReadOnlyList<(CloudEventKinds Kind, string Name, string Label)> All { get; } =
    [
        (CloudEventKinds.Joined, "joined", "Joined"),
        (CloudEventKinds.AlreadyHere, "alreadyHere", "Already here"),
        (CloudEventKinds.Left, "left", "Left"),
        (CloudEventKinds.AvatarChanged, "avatarChanged", "Avatar changed"),
        (CloudEventKinds.StoppedLogging, "stoppedLogging", "Modbot stopped logging"),
    ];

    /// <summary>The kind an observation belongs to.</summary>
    public static CloudEventKinds Of(PresenceKind kind) => kind switch
    {
        PresenceKind.Joined => CloudEventKinds.Joined,
        PresenceKind.PresenceObserved => CloudEventKinds.AlreadyHere,
        PresenceKind.Left => CloudEventKinds.Left,
        PresenceKind.AvatarChanged => CloudEventKinds.AvatarChanged,
        PresenceKind.LogStopped => CloudEventKinds.StoppedLogging,
        _ => CloudEventKinds.None,
    };
}

/// <summary>
/// What the backup sends for one kind of instance: which events, and which of their details.
/// </summary>
/// <param name="Events">The kinds of event that are sent. None means this kind of instance sends nothing.</param>
/// <param name="WorldId">Whether the world id goes with each event.</param>
/// <param name="InstanceId">Whether the instance id goes with each event.</param>
/// <param name="AvatarName">Whether an avatar change carries the avatar's name.</param>
/// <param name="GroupId">
/// Whether the group id goes with each event. Only means anything for group instances: a non-group
/// instance has no group, and this stays true there.
/// </param>
public sealed record CloudSection(
    CloudEventKinds Events = CloudEventKinds.All,
    bool WorldId = true,
    bool InstanceId = true,
    bool AvatarName = true,
    bool GroupId = true)
{
    public static CloudSection Default { get; } = new();

    /// <summary>How many of the five kinds are on.</summary>
    public int EventCount => BitOperations.PopCount((uint)(Events & CloudEventKinds.All));

    public bool Sends(CloudEventKinds kind) => (Events & kind) != CloudEventKinds.None;

    public bool Has(CloudDetail detail) => detail switch
    {
        CloudDetail.WorldId => WorldId,
        CloudDetail.InstanceId => InstanceId,
        CloudDetail.GroupId => GroupId,
        CloudDetail.AvatarName => AvatarName,
        _ => true,
    };

    public CloudSection WithEvent(CloudEventKinds kind, bool on)
        => this with { Events = on ? Events | kind : Events & ~kind };

    public CloudSection WithDetail(CloudDetail detail, bool on) => detail switch
    {
        CloudDetail.WorldId => this with { WorldId = on },
        CloudDetail.InstanceId => this with { InstanceId = on },
        CloudDetail.GroupId => this with { GroupId = on },
        CloudDetail.AvatarName => this with { AvatarName = on },
        _ => this,
    };
}

/// <summary>
/// What the Modbot Cloud backup sends, chosen on the Cloud Server page: one <see cref="CloudSection"/>
/// for the instances a group owns and one for the instances nobody's group owns.
/// </summary>
/// <remarks>
/// <para>Everything is on until somebody turns it off, so an install that never opens the page sends
/// what it always did. What is not here is not a choice: the user id, the display name, the app
/// version, the event id, the type and the times are always sent.</para>
/// <para>Saved in <c>settings.json</c> under <c>cloud</c>, as <c>group</c> and <c>nonGroup</c>. A
/// file that names no event in either leaves the backup with nothing to send (<see cref="AnyEventOn"/>).</para>
/// </remarks>
public sealed record CloudChoices(CloudSection Group, CloudSection NonGroup)
{
    public const string GroupField = "group";

    public const string NonGroupField = "nonGroup";

    public static CloudChoices Default { get; } = new(CloudSection.Default, CloudSection.Default);

    /// <summary>
    /// Whether any kind of event is on in either section. When none is, the backup has nothing it
    /// could send, so it neither registers with Cloud nor asks it the time.
    /// </summary>
    public bool AnyEventOn => Group.Events != CloudEventKinds.None || NonGroup.Events != CloudEventKinds.None;

    public CloudSection For(bool inGroup) => inGroup ? Group : NonGroup;

    /// <summary>Whether an observation is sent: its kind is on in the section for its kind of instance.</summary>
    public bool Sends(PresenceKind kind, bool inGroup) => For(inGroup).Sends(CloudEventKindNames.Of(kind));

    /// <summary>
    /// Switches one kind of event on or off. Switching off the last one that is on, across both
    /// sections, is refused: the choices come back unchanged and <c>Refused</c> says so.
    /// </summary>
    public (CloudChoices Choices, bool Refused) WithEvent(bool inGroup, CloudEventKinds kind, bool on)
    {
        var changed = inGroup
            ? this with { Group = Group.WithEvent(kind, on) }
            : this with { NonGroup = NonGroup.WithEvent(kind, on) };

        return !on && !changed.AnyEventOn && AnyEventOn ? (this, true) : (changed, false);
    }

    /// <summary>Switches one detail on or off. A non-group instance has no group, so that one does nothing there.</summary>
    public CloudChoices WithDetail(bool inGroup, CloudDetail detail, bool on)
    {
        if (!inGroup && detail is CloudDetail.GroupId)
            return this;

        return inGroup
            ? this with { Group = Group.WithDetail(detail, on) }
            : this with { NonGroup = NonGroup.WithDetail(detail, on) };
    }

    /// <summary>
    /// Reads the two objects from <c>settings.json</c>. A missing object, or a missing field in one,
    /// is on. An <c>events</c> list that is there but names nothing known is none on.
    /// </summary>
    public static CloudChoices FromJson(JsonObject? group, JsonObject? nonGroup)
        => new(SectionFromJson(group, withGroup: true), SectionFromJson(nonGroup, withGroup: false));

    /// <summary>The object saved under <c>cloud.group</c>.</summary>
    public JsonObject GroupToJson() => SectionToJson(Group, withGroup: true);

    /// <summary>The object saved under <c>cloud.nonGroup</c>.</summary>
    public JsonObject NonGroupToJson() => SectionToJson(NonGroup, withGroup: false);

    private static CloudSection SectionFromJson(JsonObject? shape, bool withGroup)
    {
        if (shape is null)
            return CloudSection.Default;

        var events = CloudEventKinds.All;
        if (shape["events"] is JsonArray listed)
        {
            events = CloudEventKinds.None;
            foreach (var item in listed)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var name))
                {
                    foreach (var known in CloudEventKindNames.All)
                    {
                        if (string.Equals(known.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                            events |= known.Kind;
                    }
                }
            }
        }

        return new CloudSection(
            events,
            Flag(shape, "worldId"),
            Flag(shape, "instanceId"),
            Flag(shape, "avatarName"),
            !withGroup || Flag(shape, "groupId"));
    }

    private static bool Flag(JsonObject shape, string field)
        => shape[field] is not JsonValue value || !value.TryGetValue<bool>(out var on) || on;

    private static JsonObject SectionToJson(CloudSection section, bool withGroup)
    {
        var events = new JsonArray();
        foreach (var (kind, file, _) in CloudEventKindNames.All)
        {
            if (section.Sends(kind))
                events.Add(file);
        }

        var shape = new JsonObject
        {
            ["events"] = events,
            ["worldId"] = section.WorldId,
            ["instanceId"] = section.InstanceId,
        };

        if (withGroup)
            shape["groupId"] = section.GroupId;

        shape["avatarName"] = section.AvatarName;
        return shape;
    }
}
