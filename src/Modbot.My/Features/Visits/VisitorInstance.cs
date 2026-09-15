using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.My.Common;

namespace Modbot.My.Features.Visits;

/// <summary>
/// An instance URL opened on my.modbot.co from one IP address. The table is <c>visitor_instance</c>.
/// </summary>
/// <remarks>
/// This is what lets a browser that has saved nothing still see its instances (central services spec
/// 2.3). Everyone behind one address shares one list.
/// </remarks>
public sealed class VisitorInstance
{
    /// <summary>The visitor's address as text, from <see cref="ClientAddress"/>.</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>The instance's origin, such as <c>https://modbot.example</c>.</summary>
    public string InstanceUrl { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The last time a save arrived, repeats included.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the visit last counted started. Saves soon after it belong to the same visit.</summary>
    public DateTimeOffset LastVisitAt { get; set; }

    public int Visits { get; set; }
}

internal sealed class VisitorInstanceConfiguration : IEntityTypeConfiguration<VisitorInstance>
{
    public void Configure(EntityTypeBuilder<VisitorInstance> entity)
    {
        entity.ToTable("visitor_instance");

        // Addresses are text rather than inet: EF Core cannot key on IPAddress. ClientAddress writes
        // one address one way, so text equality is address equality.
        entity.HasKey(v => new { v.IpAddress, v.InstanceUrl });
        entity.Property(v => v.IpAddress).HasMaxLength(ClientAddress.MaxLength);
        entity.Property(v => v.InstanceUrl).HasMaxLength(Common.InstanceUrl.MaxLength);
        entity.HasIndex(v => v.InstanceUrl);
    }
}
