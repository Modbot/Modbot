using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.PublicInstances;

/// <summary>
/// One Modbot server that tells Cloud which of its group's instances anyone can join. The table is
/// <c>instances_server</c>.
/// </summary>
/// <remarks>
/// <para>
/// The server makes up its own id and secret and sends them with its first report; Cloud keeps only
/// a hash of the secret and treats the first report under an id as the one that claims it. So a
/// server keeps its own rows and nobody else can overwrite them, and Cloud never has to hand out a
/// credential or know who an operator is.
/// </para>
/// <para>
/// Nothing here counts people. There is no member count, no head count and no person's name or id,
/// because the report has no field for any of them.
/// </para>
/// </remarks>
public sealed class InstancesServer
{
    public const int MaxGroupIdLength = 128;
    public const int MaxNameLength = 200;
    public const int MaxUrlLength = 1024;

    /// <summary>The id the Modbot server made up for itself.</summary>
    public Guid Id { get; set; }

    /// <summary>Lower-case hex SHA-256 of the secret.</summary>
    public string SecretHash { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public string? GroupIconUrl { get; set; }

    public string? GroupBannerUrl { get; set; }

    public DateTimeOffset FirstReportedAt { get; set; }

    /// <summary>When the last report arrived. A server that stops reporting drops off the page.</summary>
    public DateTimeOffset LastReportedAt { get; set; }
}

/// <summary>
/// One instance a group has open to everyone, as its Modbot last reported it. The table is
/// <c>public_instance</c>.
/// </summary>
public sealed class PublicInstance
{
    public const int MaxLocationLength = 512;
    public const int MaxRegionLength = 16;

    public Guid Id { get; set; }

    public Guid ServerId { get; set; }

    /// <summary>VRChat's location string. Unique for one server, so a report updates rather than adds.</summary>
    public string Location { get; set; } = string.Empty;

    public string WorldId { get; set; } = string.Empty;

    public string? WorldName { get; set; }

    public string? WorldImageUrl { get; set; }

    public string? JoinLink { get; set; }

    public string? Region { get; set; }

    /// <summary>When the group's Modbot first knew the instance was open.</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>When this instance was last in a report.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}

internal sealed class InstancesServerConfiguration : IEntityTypeConfiguration<InstancesServer>
{
    public void Configure(EntityTypeBuilder<InstancesServer> entity)
    {
        entity.ToTable("instances_server");
        entity.HasKey(s => s.Id);
        entity.Property(s => s.Id).ValueGeneratedNever();
        entity.Property(s => s.SecretHash).HasMaxLength(64);
        entity.Property(s => s.GroupId).HasMaxLength(InstancesServer.MaxGroupIdLength);
        entity.Property(s => s.GroupName).HasMaxLength(InstancesServer.MaxNameLength);
        entity.Property(s => s.GroupIconUrl).HasMaxLength(InstancesServer.MaxUrlLength);
        entity.Property(s => s.GroupBannerUrl).HasMaxLength(InstancesServer.MaxUrlLength);

        // The read is "every server that reported recently", so the freshness is the index.
        entity.HasIndex(s => s.LastReportedAt);

        // One row per group. Two Modbots managing the same group would otherwise list it twice, and
        // the page would show the same event beside itself.
        entity.HasIndex(s => s.GroupId).IsUnique();
    }
}

internal sealed class PublicInstanceConfiguration : IEntityTypeConfiguration<PublicInstance>
{
    public void Configure(EntityTypeBuilder<PublicInstance> entity)
    {
        entity.ToTable("public_instance");
        entity.HasKey(r => r.Id);
        entity.Property(r => r.Id).ValueGeneratedNever();
        entity.Property(r => r.Location).HasMaxLength(PublicInstance.MaxLocationLength);
        entity.Property(r => r.WorldId).HasMaxLength(PublicInstance.MaxLocationLength);
        entity.Property(r => r.WorldName).HasMaxLength(InstancesServer.MaxNameLength);
        entity.Property(r => r.WorldImageUrl).HasMaxLength(InstancesServer.MaxUrlLength);
        entity.Property(r => r.JoinLink).HasMaxLength(InstancesServer.MaxUrlLength);
        entity.Property(r => r.Region).HasMaxLength(PublicInstance.MaxRegionLength);

        entity.HasIndex(r => new { r.ServerId, r.Location }).IsUnique();
    }
}
