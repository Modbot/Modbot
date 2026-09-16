using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.Registry;

/// <summary>
/// One report a Modbot server sent. The table is <c>server_report</c>.
/// </summary>
/// <remarks>
/// Kept row by row rather than only as the latest values, so that "how many deployments were on
/// this release last month" and "did cold stops spike after VRChat changed something" are questions
/// the data can answer. A server reports every six hours, so a year of one server is about 1,500
/// rows.
/// </remarks>
public sealed class ServerReport
{
    public long Id { get; set; }

    public Guid ServerId { get; set; }

    public DateTimeOffset ReportedAt { get; set; }

    public string? PublicAddress { get; set; }

    public string? Version { get; set; }

    public string? HostPlatform { get; set; }

    public string? GroupId { get; set; }

    public string? GroupName { get; set; }

    public bool? DiscordConnected { get; set; }

    public List<string>? TermListsImported { get; set; }

    public int? RateLimitColdStops { get; set; }

    public int? WafBlocks { get; set; }

    public bool? AiModerationEnabled { get; set; }

    /// <summary>The address this report arrived from.</summary>
    public string? IpAddress { get; set; }
}

internal sealed class ServerReportConfiguration : IEntityTypeConfiguration<ServerReport>
{
    public void Configure(EntityTypeBuilder<ServerReport> entity)
    {
        entity.ToTable("server_report");
        entity.HasKey(r => r.Id);
        entity.Property(r => r.PublicAddress).HasMaxLength(InstanceUrl.MaxLength);
        entity.Property(r => r.Version).HasMaxLength(RegisteredServer.MaxVersionLength);
        entity.Property(r => r.HostPlatform).HasMaxLength(RegisteredServer.MaxPlatformLength);
        entity.Property(r => r.GroupId).HasMaxLength(RegisteredServer.MaxGroupIdLength);
        entity.Property(r => r.GroupName).HasMaxLength(RegisteredServer.MaxGroupNameLength);
        entity.Property(r => r.IpAddress).HasMaxLength(ClientAddress.MaxLength);

        // One server's reports newest first is the only way this table is read.
        entity.HasIndex(r => new { r.ServerId, r.ReportedAt });

        entity.HasOne<RegisteredServer>()
            .WithMany()
            .HasForeignKey(r => r.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
