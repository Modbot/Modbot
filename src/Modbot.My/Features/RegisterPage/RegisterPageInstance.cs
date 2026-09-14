using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.My.Features.RegisterPage;

/// <summary>
/// An instance URL noted because someone opened <c>/register</c> for it. The table is
/// <c>register_page_instance</c>.
/// </summary>
/// <remarks>
/// Kept apart from <c>registered_instance</c> (central services spec 4.1). A page visit carries a
/// URL and nothing else, so a deployment whose operator turned analytics off is still counted,
/// and it can never overwrite what a deployment reported about itself.
/// </remarks>
public sealed class RegisterPageInstance
{
    /// <summary>The instance's origin, such as <c>https://modbot.example</c>.</summary>
    public string InstanceUrl { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>How many times the register page was opened for this URL.</summary>
    public int Visits { get; set; }
}

internal sealed class RegisterPageInstanceConfiguration : IEntityTypeConfiguration<RegisterPageInstance>
{
    public void Configure(EntityTypeBuilder<RegisterPageInstance> entity)
    {
        entity.ToTable("register_page_instance");
        entity.HasKey(i => i.InstanceUrl);
        entity.Property(i => i.InstanceUrl).HasMaxLength(Common.InstanceUrl.MaxLength);
        entity.HasIndex(i => i.LastSeenAt);
    }
}
