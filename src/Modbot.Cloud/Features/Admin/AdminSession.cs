using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Admin;

/// <summary>
/// A signed-in <c>/admin</c> browser. The table is <c>admin_session</c>.
/// </summary>
/// <remarks>
/// Only a hash of the token's random part is stored, so reading the table does not let anyone sign
/// in. Logging out deletes the row.
/// </remarks>
public sealed class AdminSession
{
    /// <summary>Lower-case hex SHA-256 of the token's random part.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class AdminSessionConfiguration : IEntityTypeConfiguration<AdminSession>
{
    public void Configure(EntityTypeBuilder<AdminSession> entity)
    {
        entity.ToTable("admin_session");
        entity.HasKey(s => s.TokenHash);
        entity.Property(s => s.TokenHash).HasMaxLength(64);
        entity.HasIndex(s => s.ExpiresAt);
    }
}
