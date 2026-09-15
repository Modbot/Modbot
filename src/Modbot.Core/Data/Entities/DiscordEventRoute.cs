namespace Modbot.Core.Data.Entities;

/// <summary>
/// One rule for sending events to a Discord channel: which channel, which event types, and who
/// they must be about or done by. The table is <c>discord_event_route</c>.
/// </summary>
/// <remarks>
/// <para>
/// Discord event routes design §2 and §3. Every filter that is set must match; inside one filter
/// any listed value is enough; an empty filter matches everything. Several routes may send to the
/// same channel, and the channel still gets each event once.
/// </para>
/// <para>
/// Ids are opaque text, never parsed or checked for shape (foundation §3.1.1). The settings page
/// calls these "Channels"; "route" is the code's word only.
/// </para>
/// </remarks>
public class DiscordEventRoute
{
    public Guid Id { get; set; }

    /// <summary>A label for the list. Null shows the channel alone.</summary>
    public string? Name { get; set; }

    /// <summary>The Discord channel. Opaque snowflake.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Off keeps the route and sends nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fact types to send. Only <c>DiscordEventTypes.CanSend</c> types are ever honoured.</summary>
    public List<string> EventTypes { get; set; } = [];

    /// <summary>VRChat user ids the event must be about. Empty: anyone.</summary>
    public List<string> SubjectIds { get; set; } = [];

    /// <summary>VRChat user ids the event must be done by.</summary>
    public List<string> ActorIds { get; set; } = [];

    /// <summary>Also match events nobody did -- the ones Modbot or a sync recorded on its own.</summary>
    public bool ActorAutomatic { get; set; }

    /// <summary>VRChat group roles, any of which the person the event is about must hold now.</summary>
    public List<string> SubjectVRChatRoleIds { get; set; } = [];

    /// <summary>VRChat group roles, any of which the person who did it must hold now.</summary>
    public List<string> ActorVRChatRoleIds { get; set; } = [];

    /// <summary>Modbot roles, any of which the account of the person who did it must hold now.</summary>
    public List<Guid> ActorModbotRoleIds { get; set; } = [];

    /// <summary>Order in the list. New routes go last.</summary>
    public int Position { get; set; }

    /// <summary>Whether any filter beyond the event types is set.</summary>
    public bool HasPeopleFilters =>
        SubjectIds.Count > 0
        || ActorIds.Count > 0
        || ActorAutomatic
        || SubjectVRChatRoleIds.Count > 0
        || ActorVRChatRoleIds.Count > 0
        || ActorModbotRoleIds.Count > 0;
}

/// <summary>
/// How far one channel has been sent, and what went wrong last. The table is
/// <c>discord_event_channel</c>.
/// </summary>
/// <remarks>
/// <para>
/// One row per channel rather than one number on the settings row, so a channel whose permissions
/// were taken away waits on its own while every other channel carries on (design §5).
/// </para>
/// <para>
/// A row exists only while some enabled route sends to the channel. When the last one is turned
/// off or deleted the row goes, so turning one back on starts from then instead of replaying what
/// happened in between.
/// </para>
/// </remarks>
public class DiscordEventChannel
{
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The largest fact id this channel has read past. Posting resumes after it.</summary>
    public long PostedThrough { get; set; }

    public DateTimeOffset? LastPostedAt { get; set; }

    /// <summary>Why the last post was refused, as a sentence. Cleared by the next post that goes out.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>After a refusal, the channel is not tried again before this.</summary>
    public DateTimeOffset? RetryAt { get; set; }
}
