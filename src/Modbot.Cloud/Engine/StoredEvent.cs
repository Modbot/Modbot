using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// One presence event a companion backed up. The table is <c>client_event</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same event a Modbot server is sent</strong> (client protocol 4.2) — a join, an
/// "already here", a leave, an avatar change or a stopped log — but from every instance the
/// moderator was in, not only their group's. There is no raw log line in Cloud.
/// </para>
/// <para>
/// <strong>Never updated.</strong> Stored once, deleted only by retention.
/// </para>
/// <para>
/// <strong>De-duplicated on the client's event id, per install.</strong> The primary key is
/// <c>(install_id, client_event_id)</c>: a client keeps an event's id through every retry and
/// restart, so a batch sent twice stores nothing the second time (cloud event backup spec 4.3).
/// </para>
/// <para>
/// <strong>Not partitioned.</strong> A partitioned table cannot hold a unique key that ignores its
/// partition column, and the event id is the only thing that makes a retry exactly safe. Events are
/// a few dozen an hour per client, so a year for a hundred installs is millions of rows, and the
/// daily retention delete is small. Revisit if volume passes a hundred million rows.
/// </para>
/// <para>
/// <strong>Three times</strong> (spec 5): <see cref="ReceivedAt"/> is Cloud's own clock;
/// <see cref="SentAt"/> is the client PC's when it sent the batch; <see cref="OccurredAt"/> is when it
/// happened, in Cloud's time. <see cref="ClockAdjustmentMs"/> is what Cloud added to the client's own
/// corrected time — zero whenever the client's measure was trusted — so the client's value is
/// <c>occurred_at − clock_adjustment_ms</c>.
/// </para>
/// <para>
/// <strong>Private data.</strong> A VRChat user id and display name, and where they were. Instance
/// ids never carry the <c>nonce</c>: the client throws it away when it reads a location.
/// </para>
/// </remarks>
public sealed class StoredEvent
{
    public const int MaxEventIdLength = 64;
    public const int MaxTypeLength = 128;
    public const int MaxIdLength = 128;
    public const int MaxInstanceIdLength = 256;
    public const int MaxVersionLength = 32;

    /// <summary>The most event data kept, as UTF-8 JSON. More is stored as an empty object.</summary>
    public const int MaxDataBytes = 4096;

    public Guid InstallId { get; set; }

    /// <summary>The client's idempotency key for this event.</summary>
    public string ClientEventId { get; set; } = string.Empty;

    /// <summary>When Cloud received the batch. Cloud's clock.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When the client says it sent the batch, by the client PC's own clock, uncorrected.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>When it happened, in Cloud's time.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Upper bound of the window it happened in, or null when exact. Same adjustment applied.</summary>
    public DateTimeOffset? OccurredBefore { get; set; }

    /// <summary>What Cloud added to the client's own time to get <see cref="OccurredAt"/>.</summary>
    public int ClockAdjustmentMs { get; set; }

    /// <summary>Cloud's name for it, the same as the server's fact type, or <see cref="EventTypes.Unrecognised"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The client's own name for it, when <see cref="Type"/> is unrecognised.</summary>
    public string? TypeRaw { get; set; }

    /// <summary>The VRChat user it is about. Opaque; never checked for shape.</summary>
    public string SubjectId { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    /// <summary>User-controlled text. Treat as hostile wherever it is shown.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The owning group, or null for an instance that has none.</summary>
    public string? GroupId { get; set; }

    /// <summary>The client's release, e.g. <c>2026.9.0</c>.</summary>
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>jsonb: the display name, and the avatar name for an avatar change. Never null.</summary>
    public string Data { get; set; } = "{}";
}

internal sealed class StoredEventConfiguration : IEntityTypeConfiguration<StoredEvent>
{
    public void Configure(EntityTypeBuilder<StoredEvent> entity)
    {
        entity.ToTable("client_event");
        entity.HasKey(e => new { e.InstallId, e.ClientEventId });

        entity.Property(e => e.ClientEventId).HasMaxLength(StoredEvent.MaxEventIdLength);
        entity.Property(e => e.Type).HasMaxLength(StoredEvent.MaxTypeLength);
        entity.Property(e => e.TypeRaw).HasMaxLength(StoredEvent.MaxTypeLength);
        entity.Property(e => e.SubjectId).HasMaxLength(StoredEvent.MaxIdLength);
        entity.Property(e => e.WorldId).HasMaxLength(StoredEvent.MaxIdLength);
        entity.Property(e => e.InstanceId).HasMaxLength(StoredEvent.MaxInstanceIdLength);
        entity.Property(e => e.GroupId).HasMaxLength(StoredEvent.MaxIdLength);
        entity.Property(e => e.ClientVersion).HasMaxLength(StoredEvent.MaxVersionLength);
        entity.Property(e => e.Data).HasColumnType("jsonb");

        // The trends index: events of one type across a range of time.
        entity.HasIndex(e => new { e.Type, e.OccurredAt })
            .HasDatabaseName("ix_client_event_type_occurred_at");

        // One install's recent events, for admin.
        entity.HasIndex(e => new { e.InstallId, e.ReceivedAt })
            .HasDatabaseName("ix_client_event_install_id_received_at")
            .IsDescending(false, true);

        // The daily retention delete.
        entity.HasIndex(e => e.ReceivedAt)
            .HasDatabaseName("ix_client_event_received_at");
    }
}

/// <summary>Events stored per install per day. The table is <c>event_day_total</c>; the admin chart reads it.</summary>
/// <remarks>
/// Added to in the same transaction as the events and only for events actually stored, so a
/// duplicate is never counted. Kept after retention: a count and a random install id.
/// </remarks>
public sealed class EventDayTotal
{
    /// <summary>The UTC day Cloud received the events.</summary>
    public DateOnly Day { get; set; }

    public Guid InstallId { get; set; }

    public long Events { get; set; }
}

/// <summary>Events per type per hour across every install. The table is <c>event_hour_total</c>.</summary>
/// <remarks>
/// What trends will read first: "events of type X per hour" is a primary key range scan here
/// (cloud event backup spec 9). <see cref="Hour"/> is when the event happened, in Cloud's time,
/// truncated to the hour. No names, no ids, so it is kept forever.
/// </remarks>
public sealed class EventHourTotal
{
    public string Type { get; set; } = string.Empty;

    public DateTimeOffset Hour { get; set; }

    public long Events { get; set; }
}

internal sealed class EventDayTotalConfiguration : IEntityTypeConfiguration<EventDayTotal>
{
    public void Configure(EntityTypeBuilder<EventDayTotal> entity)
    {
        entity.ToTable("event_day_total");
        entity.HasKey(t => new { t.Day, t.InstallId });
        entity.HasIndex(t => new { t.InstallId, t.Day });
    }
}

internal sealed class EventHourTotalConfiguration : IEntityTypeConfiguration<EventHourTotal>
{
    public void Configure(EntityTypeBuilder<EventHourTotal> entity)
    {
        entity.ToTable("event_hour_total");
        entity.HasKey(t => new { t.Type, t.Hour });
        entity.Property(t => t.Type).HasMaxLength(StoredEvent.MaxTypeLength);
    }
}
