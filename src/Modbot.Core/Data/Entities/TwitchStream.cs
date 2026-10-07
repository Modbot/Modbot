namespace Modbot.Core.Data.Entities;

/// <summary>
/// One time the channel was live on Twitch, seen by the poll (Twitch design, step 1). The table is
/// <c>twitch_stream</c>: one row per Twitch stream id, written when it is first seen live.
/// </summary>
/// <remarks>
/// <para>
/// The row is also the key the "live" post is made under: a restart during a stream finds the row
/// and does nothing, and a flapping stream that keeps its Twitch id stays one row.
/// </para>
/// <para>
/// Everything but the id and the times is what Twitch said, kept as Twitch said it and never
/// validated (foundation §3.1.1 reads the same for ids).
/// </para>
/// </remarks>
public class TwitchStream
{
    public const int MaxTitleLength = 200;

    /// <summary>Twitch's stream id. Opaque text, never parsed.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When Twitch says the stream started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the poll first saw it, by <c>IModbotClock</c>.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The last poll that found it live. A stream whose last poll found it gone has an <see cref="EndedAt"/>.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the poll found the channel no longer live. Null while it is.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Twitch's word for what it is: <c>live</c>, or another (a rerun) that never gets a post.</summary>
    public string Type { get; set; } = "live";

    public string? Title { get; set; }

    /// <summary>The category (game) name Twitch shows.</summary>
    public string? Category { get; set; }

    /// <summary>The viewer count at the last poll.</summary>
    public int Viewers { get; set; }

    /// <summary>The most viewers any poll saw.</summary>
    public int PeakViewers { get; set; }

    /// <summary>The calendar event that was on at the time, or null (none, or several).</summary>
    public Guid? EventId { get; set; }

    /// <summary>A person set or cleared <see cref="EventId"/>: the poll leaves it as it is.</summary>
    public bool EventSetByStaff { get; set; }

    /// <summary>
    /// When Modbot decided about a "live" post for this stream: made one, or found nothing to make
    /// (no site ticked, too soon after the last). Null until it did. Decided once.
    /// </summary>
    public DateTimeOffset? PostDecidedAt { get; set; }

    /// <summary>The "live" post made for it, or null.</summary>
    public Guid? PostId { get; set; }

    /// <summary>When the viewer count last went out as a live update.</summary>
    public DateTimeOffset? UpdateSentAt { get; set; }
}
