using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// One VRChat log file one install has sent lines from. The table is <c>log_file</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This row is the dedupe key.</strong> <c>log_line</c> is partitioned by when Cloud
/// received a line, and a unique index on a partitioned table has to include that time, which a
/// retry never repeats. So instead each file keeps <see cref="StoredThrough"/>: the highest offset
/// stored. Clients send a file's lines in offset order, so a line at or below it has been stored
/// already (cloud log backup spec 4.4). The row is locked while a batch is written, so two
/// requests from one install cannot both store the same line.
/// </para>
/// <para>
/// Only the file name is kept, never a folder: the folder holds the Windows account name, and
/// the client does not send it.
/// </para>
/// </remarks>
public sealed class LogFile
{
    public const int MaxNameLength = 128;

    public long Id { get; set; }

    public Guid InstallId { get; set; }

    /// <summary>VRChat's own name for the file, e.g. <c>output_log_2026-09-03_20-26-45.txt</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The highest line offset stored from this file, or -1 before the first.</summary>
    public long StoredThrough { get; set; } = -1;

    public long LinesStored { get; set; }

    public DateTimeOffset FirstReceivedAt { get; set; }

    public DateTimeOffset LastReceivedAt { get; set; }
}

internal sealed class LogFileConfiguration : IEntityTypeConfiguration<LogFile>
{
    public void Configure(EntityTypeBuilder<LogFile> entity)
    {
        entity.ToTable("log_file");
        entity.HasKey(f => f.Id);
        entity.Property(f => f.Id).UseIdentityAlwaysColumn();
        entity.Property(f => f.Name).HasMaxLength(LogFile.MaxNameLength);
        entity.HasIndex(f => new { f.InstallId, f.Name }).IsUnique();
        entity.HasIndex(f => f.LastReceivedAt);
    }
}
