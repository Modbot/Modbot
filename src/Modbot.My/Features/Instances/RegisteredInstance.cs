using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.My.Features.Instances;

/// <summary>
/// A deployment that registered itself through <c>POST /api/instances/register</c>. The table is
/// <c>registered_instance</c>.
/// </summary>
/// <remarks>
/// The absent fields are the point (central services spec 4.3). There is no group id, no group name,
/// no operator identity and no credential here, because the row has nowhere to put them.
/// </remarks>
public sealed class RegisteredInstance
{
    /// <summary>Random id the deployment assigned itself on first boot.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The deployment's origin, such as <c>https://modbot.example</c>.</summary>
    public string InstanceUrl { get; set; } = string.Empty;

    public string? Version { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// The address its last register or usage call came from. Every address it has used is in
    /// <c>registered_instance_ip</c>.
    /// </summary>
    public string? IpAddress { get; set; }

    // ── Usage analytics (spec 5.2). All empty for a deployment that has never reported. ──

    public bool AnalyticsEnabled { get; set; }

    public DateTimeOffset? LastUsageReportAt { get; set; }

    /// <summary>Bucketed member count, never an exact figure.</summary>
    public string? ScaleBucket { get; set; }

    public int? PairedClients { get; set; }

    public bool? DiscordConnected { get; set; }

    public List<string>? TermListsImported { get; set; }

    public int? RateLimitColdStops { get; set; }

    public int? WafBlocks { get; set; }
}

internal sealed class RegisteredInstanceConfiguration : IEntityTypeConfiguration<RegisteredInstance>
{
    public void Configure(EntityTypeBuilder<RegisteredInstance> entity)
    {
        entity.ToTable("registered_instance");
        entity.HasKey(i => i.InstanceId);

        entity.Property(i => i.InstanceId).HasMaxLength(InstanceEndpoints.MaxIdLength);
        entity.Property(i => i.InstanceUrl).HasMaxLength(Common.InstanceUrl.MaxLength);
        entity.Property(i => i.Version).HasMaxLength(InstanceEndpoints.MaxVersionLength);
        entity.Property(i => i.ScaleBucket).HasMaxLength(32);
        entity.Property(i => i.IpAddress).HasMaxLength(Common.ClientAddress.MaxLength);

        // The list reads newest first, and the stats count what was seen in the last 30 days.
        entity.HasIndex(i => i.LastSeenAt);
        entity.HasIndex(i => i.InstanceUrl);
    }
}
