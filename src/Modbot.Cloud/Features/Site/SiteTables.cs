using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.Cloud.Common;

namespace Modbot.Cloud.Features.Site;

/// <summary>
/// A Modbot address noted because somebody opened a my.modbot.co page for it. The table is
/// <c>page_instance</c>.
/// </summary>
/// <remarks>
/// Kept apart from <c>registered_server</c> on purpose (central services spec 4.1). A page visit
/// carries an address and nothing else, so a Modbot whose operator set
/// <c>MODBOT_CLOUD_DISABLED=1</c> is still counted, and a visit can never overwrite what a server
/// reported about itself.
/// </remarks>
public sealed class PageInstance
{
    /// <summary>The Modbot's origin, such as <c>https://modbot.example</c>.</summary>
    public string InstanceUrl { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>How many times a page was opened for this address.</summary>
    public int Visits { get; set; }
}

internal sealed class PageInstanceConfiguration : IEntityTypeConfiguration<PageInstance>
{
    public void Configure(EntityTypeBuilder<PageInstance> entity)
    {
        entity.ToTable("page_instance");
        entity.HasKey(i => i.InstanceUrl);
        entity.Property(i => i.InstanceUrl).HasMaxLength(InstanceUrl.MaxLength);
        entity.HasIndex(i => i.LastSeenAt);
    }
}

/// <summary>
/// A Modbot address opened from one visitor's IP address. The table is <c>visitor_instance</c>.
/// </summary>
/// <remarks>
/// This is what lets a browser that has saved nothing still see its servers (central services spec
/// 2.3.1). Everyone behind one address shares one list, which is the cost of the feature rather than
/// a fault in it.
/// </remarks>
public sealed class VisitorInstance
{
    public string IpAddress { get; set; } = string.Empty;

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

        // Addresses are text rather than inet: EF Core cannot key on IPAddress, and my.modbot.co
        // writes one address one way, so text equality is address equality.
        entity.HasKey(v => new { v.IpAddress, v.InstanceUrl });
        entity.Property(v => v.IpAddress).HasMaxLength(ClientAddress.MaxLength);
        entity.Property(v => v.InstanceUrl).HasMaxLength(InstanceUrl.MaxLength);
        entity.HasIndex(v => v.InstanceUrl);
    }
}
