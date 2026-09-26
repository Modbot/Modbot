using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.Cloud.Common;
using Modbot.Cloud.Features.Accounts;

namespace Modbot.Cloud.Features.Registry;

/// <summary>
/// One Modbot server that registered itself. The table is <c>registered_server</c>.
/// </summary>
/// <remarks>
/// <para>
/// The newest values from its reports, so a list is one read. Every report is also kept in
/// <see cref="ServerReport"/>, so the figures can be charted.
/// </para>
/// <para>
/// <strong>What is absent is the point.</strong> There is no column for the number of users, the
/// number of group members, or the number of people in a VRChat instance, and no column for any
/// member identity, moderation record, fact, profile text or credential. Apart from the group this
/// row says nothing about anybody (Cloud accounts and registry spec 3.2).
/// </para>
/// </remarks>
public sealed class RegisteredServer
{
    public const int MaxVersionLength = 64;
    public const int MaxPlatformLength = 64;
    public const int MaxGroupIdLength = 128;
    public const int MaxGroupNameLength = 200;
    public const int MaxGroupDescriptionLength = 2000;
    public const int MaxUrlLength = 1024;

    /// <summary>Assigned by Cloud at registration, so nobody can take an id that is not theirs.</summary>
    public Guid Id { get; set; }

    /// <summary>Lower-case hex SHA-256 of the secret handed back once at registration.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The server's origin, such as <c>https://modbot.example</c>.</summary>
    public string? PublicAddress { get; set; }

    public string? Version { get; set; }

    /// <summary>The operating system and architecture the process runs on.</summary>
    public string? HostPlatform { get; set; }

    // ── The group this Modbot moderates. The one thing a report is not anonymous about. ──

    public string? GroupId { get; set; }

    public string? GroupName { get; set; }

    public string? GroupDescription { get; set; }

    public string? GroupIconUrl { get; set; }

    public string? GroupBannerUrl { get; set; }

    // ── Plain figures. Never a count of people. ──

    public bool? DiscordConnected { get; set; }

    /// <summary>Which lists are imported — the ids, never their contents.</summary>
    public List<string>? TermListsImported { get; set; }

    public int? RateLimitColdStops { get; set; }

    public int? WafBlocks { get; set; }

    public bool? AiModerationEnabled { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>When a report last arrived, or null before the first one.</summary>
    public DateTimeOffset? LastReportAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The address its last call came from. Every call's address is on its report row.</summary>
    public string? IpAddress { get; set; }

    // ── Claiming (spec 3.3) ──

    /// <summary>The account that claimed this server, or null while nobody has.</summary>
    public Guid? AccountId { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// Lower-case hex SHA-256 of the link code the server is currently showing its owner, or null.
    /// The code itself is never sent to Cloud and is never stored.
    /// </summary>
    public string? LinkCodeHash { get; set; }

    public DateTimeOffset? LinkCodeExpiresAt { get; set; }
}

internal sealed class RegisteredServerConfiguration : IEntityTypeConfiguration<RegisteredServer>
{
    public void Configure(EntityTypeBuilder<RegisteredServer> entity)
    {
        entity.ToTable("registered_server");
        entity.HasKey(s => s.Id);
        entity.Property(s => s.Id).ValueGeneratedNever();
        entity.Property(s => s.SecretHash).HasMaxLength(64);
        entity.Property(s => s.PublicAddress).HasMaxLength(ServerUrl.MaxLength);
        entity.Property(s => s.Version).HasMaxLength(RegisteredServer.MaxVersionLength);
        entity.Property(s => s.HostPlatform).HasMaxLength(RegisteredServer.MaxPlatformLength);
        entity.Property(s => s.GroupId).HasMaxLength(RegisteredServer.MaxGroupIdLength);
        entity.Property(s => s.GroupName).HasMaxLength(RegisteredServer.MaxGroupNameLength);
        entity.Property(s => s.GroupDescription).HasMaxLength(RegisteredServer.MaxGroupDescriptionLength);
        entity.Property(s => s.GroupIconUrl).HasMaxLength(RegisteredServer.MaxUrlLength);
        entity.Property(s => s.GroupBannerUrl).HasMaxLength(RegisteredServer.MaxUrlLength);
        entity.Property(s => s.IpAddress).HasMaxLength(ClientAddress.MaxLength);
        entity.Property(s => s.LinkCodeHash).HasMaxLength(64);

        entity.HasIndex(s => s.LastSeenAt);
        entity.HasIndex(s => s.PublicAddress);
        entity.HasIndex(s => s.AccountId);

        // Only one server can be waiting on a given code at a time, and a lookup by code is an
        // index seek rather than a scan of every server.
        entity.HasIndex(s => s.LinkCodeHash).IsUnique();

        // Deleting an account leaves its servers registered and unclaimed. The server keeps working
        // and its owner can claim it again; deleting somebody else's Modbot from the registry
        // because they closed an account would be the wrong kind of tidy.
        entity.HasOne<Account>()
            .WithMany()
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
