using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// How far one install's clock is off from Cloud's, as last measured. The table is
/// <c>install_clock</c>, one row per install, updated with every batch.
/// </summary>
/// <remarks>
/// <para>
/// The server's approach (foundation 4.4), kept both ways: the client's own SNTP estimate against
/// Cloud's <c>/api/v1/time</c>, and Cloud's measurement of the same thing, which is when it
/// received a batch minus when the client says it sent it. The second includes one request's
/// network delay and cannot be faked by a client that measured badly.
/// </para>
/// <para>
/// A PC whose two numbers disagree by more than <see cref="DisagreeAbove"/> is flagged, not
/// refused: its lines are still kept, with their raw times, and analytics can leave them out.
/// </para>
/// </remarks>
public sealed class InstallClock
{
    public static readonly TimeSpan DisagreeAbove = TimeSpan.FromMinutes(5);

    public const int MaxConfidenceLength = 16;

    public Guid InstallId { get; set; }

    /// <summary>What the client measured: add this to its clock to get Cloud's.</summary>
    public long ReportedOffsetMs { get; set; }

    /// <summary><c>unknown</c>, <c>poor</c>, <c>fair</c> or <c>good</c>, as the client said.</summary>
    public string ReportedConfidence { get; set; } = "unknown";

    /// <summary>What Cloud measured: received time minus the client's sent time.</summary>
    public long ObservedOffsetMs { get; set; }

    /// <summary>The correction applied to this install's latest events.</summary>
    public long AppliedOffsetMs { get; set; }

    public bool Disagrees { get; set; }

    public long Batches { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class InstallClockConfiguration : IEntityTypeConfiguration<InstallClock>
{
    public void Configure(EntityTypeBuilder<InstallClock> entity)
    {
        entity.ToTable("install_clock");
        entity.HasKey(c => c.InstallId);
        entity.Property(c => c.InstallId).ValueGeneratedNever();
        entity.Property(c => c.ReportedConfidence).HasMaxLength(InstallClock.MaxConfidenceLength);
        entity.HasIndex(c => c.UpdatedAt);
    }
}
