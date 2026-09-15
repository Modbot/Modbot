using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// What a parser understood one <see cref="LogLine"/> to mean. The table is <c>log_event</c>,
/// partitioned by month on <see cref="ReceivedAt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stored the way the server stores facts</strong> (foundation 5.3.1): <see cref="Type"/> is
/// a hierarchical string, and a type Cloud has no name for is kept as
/// <see cref="LogEventTypes.Unrecognised"/> with the parser's own word in <see cref="TypeRaw"/> and
/// its data whole. It can be understood later.
/// </para>
/// <para>
/// <strong>Never updated.</strong> A newer parser reading the same line writes a new row with its
/// own <see cref="ParsedBy"/>.
/// </para>
/// <para>
/// The index on <c>(type, occurred_at)</c> is what "events of type X per hour across all installs"
/// reads when <see cref="EventHourTotal"/> does not answer it (cloud log backup spec 9).
/// </para>
/// </remarks>
public sealed class LogEvent
{
    public const int MaxTypeLength = 128;
    public const int MaxParsedByLength = 32;

    /// <summary>The most event data kept, as UTF-8 JSON. More is stored as an empty object.</summary>
    public const int MaxDataBytes = 4096;

    public long Id { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// When it happened, in Cloud's time: the log's timestamp read with the PC's offset, then
    /// corrected for the PC's clock. Null when the line had no usable time.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; set; }

    public Guid InstallId { get; set; }

    public long LogFileId { get; set; }

    /// <summary>With <see cref="LogFileId"/>, finds the raw line.</summary>
    public long LineOffset { get; set; }

    public string Type { get; set; } = string.Empty;

    /// <summary>The parser's own name for the event, when <see cref="Type"/> is unrecognised.</summary>
    public string? TypeRaw { get; set; }

    /// <summary>Which parser produced this: <c>client/2026.9.0</c>.</summary>
    public string ParsedBy { get; set; } = string.Empty;

    /// <summary>jsonb. Never null; an empty object instead.</summary>
    public string Data { get; set; } = "{}";
}

internal sealed class LogEventConfiguration : IEntityTypeConfiguration<LogEvent>
{
    public void Configure(EntityTypeBuilder<LogEvent> entity)
    {
        entity.ToTable("log_event");
        entity.HasKey(e => new { e.Id, e.ReceivedAt });
        entity.Property(e => e.Id).UseSerialColumn();
        entity.Property(e => e.Type).HasMaxLength(LogEvent.MaxTypeLength);
        entity.Property(e => e.TypeRaw).HasMaxLength(LogEvent.MaxTypeLength);
        entity.Property(e => e.ParsedBy).HasMaxLength(LogEvent.MaxParsedByLength);
        entity.Property(e => e.Data).HasColumnType("jsonb");

        // The trends index.
        entity.HasIndex(e => new { e.Type, e.OccurredAt })
            .HasDatabaseName("ix_log_event_type_occurred_at");
    }
}
