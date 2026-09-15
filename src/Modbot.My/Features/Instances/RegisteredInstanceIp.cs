using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.My.Common;

namespace Modbot.My.Features.Instances;

/// <summary>
/// An IP address a registered deployment called from. The table is <c>registered_instance_ip</c>.
/// </summary>
/// <remarks>
/// One row per deployment and address, so a deployment that moves host starts a new row and the old
/// one keeps when it was last used.
/// </remarks>
public sealed class RegisteredInstanceIp
{
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The address as text, from <see cref="ClientAddress"/>.</summary>
    public string IpAddress { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Register and usage calls from this address.</summary>
    public int Requests { get; set; }
}

internal sealed class RegisteredInstanceIpConfiguration : IEntityTypeConfiguration<RegisteredInstanceIp>
{
    public void Configure(EntityTypeBuilder<RegisteredInstanceIp> entity)
    {
        entity.ToTable("registered_instance_ip");
        entity.HasKey(i => new { i.InstanceId, i.IpAddress });
        entity.Property(i => i.InstanceId).HasMaxLength(InstanceEndpoints.MaxIdLength);
        entity.Property(i => i.IpAddress).HasMaxLength(ClientAddress.MaxLength);

        // Deleting a deployment deletes where it called from.
        entity.HasOne<RegisteredInstance>()
            .WithMany()
            .HasForeignKey(i => i.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
