using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// Lines stored per install per day. The table is <c>line_day_total</c>. The admin chart reads it.
/// </summary>
/// <remarks>
/// Added to in the same transaction as the lines, and only for lines actually stored, so a
/// duplicate is never counted. Kept after retention drops the lines: it holds a count and a
/// random install id, nothing from the log.
/// </remarks>
public sealed class LineDayTotal
{
    /// <summary>The UTC day Cloud received the lines.</summary>
    public DateOnly Day { get; set; }

    public Guid InstallId { get; set; }

    public long Lines { get; set; }
}

/// <summary>
/// Parsed events per type per hour, across every install. The table is <c>event_hour_total</c>.
/// </summary>
/// <remarks>
/// What trends will read first: "events of type X per hour" is a primary key range scan here
/// (cloud log backup spec 9). Added to in the same transaction as the events. <see cref="Hour"/> is
/// when the event happened, truncated to the hour in UTC, or when Cloud received it when the line
/// had no usable time. No names, no ids, so it is kept forever.
/// </remarks>
public sealed class EventHourTotal
{
    public string Type { get; set; } = string.Empty;

    public DateTimeOffset Hour { get; set; }

    public long Events { get; set; }
}

internal sealed class LineDayTotalConfiguration : IEntityTypeConfiguration<LineDayTotal>
{
    public void Configure(EntityTypeBuilder<LineDayTotal> entity)
    {
        entity.ToTable("line_day_total");
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
        entity.Property(t => t.Type).HasMaxLength(LogEvent.MaxTypeLength);
    }
}
