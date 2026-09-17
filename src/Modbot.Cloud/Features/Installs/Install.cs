using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Installs;

/// <summary>
/// One copy of the companion that registered to send its logs. The table is <c>install</c>.
/// </summary>
/// <remarks>
/// <para>
/// An install is a random id, not a person: nothing here names the moderator, their VRChat account
/// or their machine, and the IP address it registered from is not kept (cloud event backup spec 3.2).
/// </para>
/// <para>
/// Only a SHA-256 of the secret is stored, so reading this table does not let anyone send as the
/// install.
/// </para>
/// </remarks>
public sealed class Install
{
    public const int MaxVersionLength = 32;
    public const int MaxPlatformLength = 16;
    public const int MaxServerIdLength = 128;

    public Guid Id { get; set; }

    /// <summary>Lower-case hex SHA-256 of the secret.</summary>
    public string SecretHash { get; set; } = string.Empty;

    public string CompanionVersion { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// The paired Modbot server's id, as the client last reported it, or null when the client is not
    /// paired or its server has no id.
    /// </summary>
    public string? ModbotServerId { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>When a batch last arrived. Updated at most once a minute.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

internal sealed class InstallConfiguration : IEntityTypeConfiguration<Install>
{
    public void Configure(EntityTypeBuilder<Install> entity)
    {
        entity.ToTable("install");
        entity.HasKey(i => i.Id);
        entity.Property(i => i.Id).ValueGeneratedNever();
        entity.Property(i => i.SecretHash).HasMaxLength(64);
        entity.Property(i => i.CompanionVersion).HasMaxLength(Install.MaxVersionLength);
        entity.Property(i => i.Platform).HasMaxLength(Install.MaxPlatformLength);
        entity.Property(i => i.ModbotServerId).HasMaxLength(Install.MaxServerIdLength);
        entity.HasIndex(i => i.LastSeenAt);
    }
}
