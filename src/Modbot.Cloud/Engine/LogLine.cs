using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// One line of a VRChat log, as a client sent it. The table is <c>log_line</c>, partitioned by
/// month on <see cref="ReceivedAt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never updated.</strong> A line is stored once and dropped only with its whole month's
/// partition when retention reaches it. A later parser reads these and writes new
/// <see cref="LogEvent"/> rows; it does not touch the line.
/// </para>
/// <para>
/// <strong>Partitioned by Cloud's received time, not the log's.</strong> The log's time comes from
/// a PC Modbot does not control and can carry any date, so partitions by it could be asked for any
/// month at all. Cloud's own clock is always now, and retention is about how long Cloud has held a
/// line (cloud log backup spec 4.3).
/// </para>
/// <para>
/// <strong>Three times</strong> (spec 5): <see cref="ReceivedAt"/> is Cloud's and is trusted for
/// order; <see cref="SentAt"/> is the client PC's and is kept to measure that PC's clock;
/// <see cref="LoggedAt"/> is the text VRChat wrote, with <see cref="UtcOffsetMinutes"/> to read it.
/// </para>
/// <para>
/// <strong>Private data.</strong> <see cref="Text"/> holds other players' names and user ids and
/// private instance locations. It is read only through admin, which never shows a location as a
/// join link (spec 10).
/// </para>
/// </remarks>
public sealed class LogLine
{
    public const int MaxTextLength = 16_384;

    public long Id { get; set; }

    /// <summary>When Cloud received the batch. Cloud's clock; the partition key.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When the client says it sent the batch, by the client PC's own clock, uncorrected.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>The timestamp as VRChat wrote it: a wall clock with no offset. Null when the line has none.</summary>
    public DateTime? LoggedAt { get; set; }

    /// <summary>The client PC's UTC offset at <see cref="LoggedAt"/>, in minutes.</summary>
    public short? UtcOffsetMinutes { get; set; }

    public Guid InstallId { get; set; }

    public long LogFileId { get; set; }

    /// <summary>Byte offset of the line's first byte in its file.</summary>
    public long LineOffset { get; set; }

    public string Text { get; set; } = string.Empty;
}

internal sealed class LogLineConfiguration : IEntityTypeConfiguration<LogLine>
{
    public void Configure(EntityTypeBuilder<LogLine> entity)
    {
        entity.ToTable("log_line");

        // PostgreSQL requires the partition key in every unique constraint on a partitioned table.
        entity.HasKey(l => new { l.Id, l.ReceivedAt });

        // A plain sequence: the table is created by hand-written SQL because EF cannot express
        // declarative partitioning, and identity columns are not accepted on a partitioned parent.
        entity.Property(l => l.Id).UseSerialColumn();

        entity.Property(l => l.LoggedAt).HasColumnType("timestamp without time zone");

        // Per-install recent lines, for admin.
        entity.HasIndex(l => new { l.InstallId, l.ReceivedAt })
            .HasDatabaseName("ix_log_line_install_id_received_at")
            .IsDescending(false, true);
    }
}
