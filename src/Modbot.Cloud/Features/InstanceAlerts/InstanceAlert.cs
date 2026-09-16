using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.InstanceAlerts;

/// <summary>
/// Cloud watching one Modbot deployment from outside, and who to email when it goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists beside Modbot's own health alerts.</strong> A Modbot cannot email anybody
/// about being down: the process that would send the mail is the process that is not running, and
/// the database that holds the address is the one that cannot be reached. Cloud is the only side
/// that can see those, and it sees them the simplest way there is — the logs stopped arriving.
/// </para>
/// <para>
/// <strong>What Cloud can see, and what it cannot.</strong> It sees silence, and it sees what is in
/// the lines it did receive. It cannot see whether VRChat is reachable, whether the Discord bot is
/// connected, or how big the database is; those are Modbot's own to watch, and it can, because it is
/// up. The two halves are complements, not copies.
/// </para>
/// <para>
/// <strong>The address is typed in for now.</strong> Cloud has accounts and an instance registry,
/// but a registered instance is not yet the same thing as a log-sending install — a deployment
/// registers with Cloud twice, once for the registry and once for its logs — so nothing here can
/// resolve an install id to an account. When those two are joined, <see cref="Email"/> defaults to
/// the linked account's address and is the only field that changes.
/// </para>
/// </remarks>
public sealed class InstanceAlert
{
    public const int MaxEmailLength = 320;
    public const int MaxDetailLength = 512;

    public const int DefaultSilentAfterMinutes = 60;
    public const int DefaultQuietHours = 6;

    /// <summary>The most errors in an hour before the error check fires. 0 turns it off.</summary>
    public const int DefaultErrorsAnHour = 0;

    /// <summary>Which deployment. No foreign key: the installs live in the same database, the logs do not.</summary>
    public Guid InstallId { get; set; }

    /// <summary>Whether Cloud emails about this deployment at all.</summary>
    public bool On { get; set; }

    /// <summary>Where the emails go.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// How long Cloud may hear nothing from the deployment before that counts as a problem. An hour
    /// by default: a Modbot that is up sends a batch within a minute or two of writing anything, and
    /// a quiet deployment still writes its own hourly housekeeping lines.
    /// </summary>
    public int SilentAfterMinutes { get; set; } = DefaultSilentAfterMinutes;

    /// <summary>Errors in the last hour before that counts as a problem. 0 turns the check off.</summary>
    public int ErrorsAnHour { get; set; } = DefaultErrorsAnHour;

    /// <summary>How long Cloud stays quiet after an email about this deployment.</summary>
    public int QuietHours { get; set; } = DefaultQuietHours;

    /// <summary>True while something is wrong.</summary>
    public bool Problem { get; set; }

    /// <summary>When it started.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>What is wrong, in one sentence.</summary>
    public string? Detail { get; set; }

    /// <summary>When the last email went. Null means none has.</summary>
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>Why the last email could not be sent. Null once one goes.</summary>
    public string? LastError { get; set; }
}

internal sealed class InstanceAlertConfiguration : IEntityTypeConfiguration<InstanceAlert>
{
    public void Configure(EntityTypeBuilder<InstanceAlert> entity)
    {
        entity.ToTable("instance_alert");

        entity.HasKey(a => a.InstallId);
        entity.Property(a => a.InstallId).ValueGeneratedNever();

        // Named by hand: the convention would give "on", a reserved word in PostgreSQL. EF quotes
        // it and it works; hand-written SQL against this table would not.
        entity.Property(a => a.On).HasColumnName("watched");

        entity.Property(a => a.Email).HasMaxLength(InstanceAlert.MaxEmailLength);
        entity.Property(a => a.Detail).HasMaxLength(InstanceAlert.MaxDetailLength);
        entity.Property(a => a.LastError).HasMaxLength(InstanceAlert.MaxDetailLength);

        // The checker reads only the ones that are on, and there are as many rows as deployments.
        entity.HasIndex(a => a.On).HasDatabaseName("ix_instance_alert_watched");
    }
}
