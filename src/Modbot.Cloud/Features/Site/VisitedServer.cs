using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.Cloud.Common;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Registry;

namespace Modbot.Cloud.Features.Site;

/// <summary>
/// What a my.modbot.co register visit learned about a Modbot address by asking it. The table is
/// <c>visited_server</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="RegisteredServer"/> for the same reason <see cref="PageInstance"/> is
/// (central services spec 4.1): a visit can never overwrite what a server reported about itself.
/// A report arrives over a connection the server opened carrying its own secret; this arrives
/// because somebody opened a link, and my.modbot.co asked the address whatever answered there.
/// </para>
/// <para>
/// <strong>Where both know something, the registry wins, field by field</strong> (register details
/// spec 3.2). A registered server that has not finished setting up has no group yet, and a name
/// learned from a visit is better than no name; but where the registry has a value, that value came
/// from the server itself with its secret, and this one did not.
/// </para>
/// <para>
/// <strong><see cref="OwnerEmail"/> is admin-only.</strong> It is stored so the maintainer can reach
/// an operator. Nothing under <c>/api/v1/site</c> returns it, no page renders it, and no log line
/// carries it.
/// </para>
/// </remarks>
public sealed class VisitedServer
{
    /// <summary>The Modbot's origin, such as <c>https://modbot.example</c>.</summary>
    public string InstanceUrl { get; set; } = string.Empty;

    public string? GroupId { get; set; }

    public string? GroupName { get; set; }

    public string? GroupIconUrl { get; set; }

    public string? GroupBannerUrl { get; set; }

    /// <summary>Who runs it, when the server says. Read by <c>/admin</c> and by nothing else.</summary>
    public string? OwnerEmail { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

internal sealed class VisitedServerConfiguration : IEntityTypeConfiguration<VisitedServer>
{
    public void Configure(EntityTypeBuilder<VisitedServer> entity)
    {
        entity.ToTable("visited_server");
        entity.HasKey(s => s.InstanceUrl);
        entity.Property(s => s.InstanceUrl).HasMaxLength(InstanceUrl.MaxLength);
        entity.Property(s => s.GroupId).HasMaxLength(RegisteredServer.MaxGroupIdLength);
        entity.Property(s => s.GroupName).HasMaxLength(RegisteredServer.MaxGroupNameLength);
        entity.Property(s => s.GroupIconUrl).HasMaxLength(RegisteredServer.MaxUrlLength);
        entity.Property(s => s.GroupBannerUrl).HasMaxLength(RegisteredServer.MaxUrlLength);
        entity.Property(s => s.OwnerEmail).HasMaxLength(Account.MaxEmailLength);
        entity.HasIndex(s => s.LastSeenAt);
    }
}
