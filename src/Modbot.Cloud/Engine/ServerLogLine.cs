using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Engine;

/// <summary>
/// One log line a Modbot deployment sent Cloud. The table is <c>server_log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The server log feed the cloud event backup spec 0 set aside for later: each Modbot deployment
/// sends its own structured log to Cloud, so the project can help with a problem on a deployment it
/// cannot reach, and so an operator whose container was thrown away still has the log.
/// </para>
/// <para>
/// <strong>The same lines the deployment keeps itself</strong>, unchanged: whatever level it records
/// at — Information and above unless its <c>LOG_LEVEL</c> asks for more — outbound API traffic left
/// out, secret-looking properties already replaced before they left. It is on by default and the
/// operator can turn it off.
/// </para>
/// <para>
/// <strong>Partitioned by month on <see cref="ReceivedAt"/></strong>, which is Cloud's own clock.
/// Not on <see cref="At"/>: that is the sending deployment's clock, and a deployment whose clock is
/// years out would write into a partition that does not exist and fail the whole batch. Retention
/// drops whole months, which is the only affordable way to prune a table this size — log lines are
/// hundreds of times more numerous than the presence events beside them, which is why this table is
/// partitioned and <see cref="StoredEvent"/> is not.
/// </para>
/// <para>
/// <strong>Never updated.</strong> A log line is what was written at the time.
/// </para>
/// <para>
/// <see cref="ServerId"/> is the id the deployment registered with in the server registry — the same
/// credential it reports with. That is what lets the account which claimed that server read its own
/// logs, which is the whole point of Cloud holding them.
/// </para>
/// </remarks>
public sealed class ServerLogLine
{
    public const int MaxLevelLength = 16;
    public const int MaxSourceLength = 256;
    public const int MaxAreaLength = 32;
    public const int MaxServiceLength = 32;
    public const int MaxVersionLength = 32;

    /// <summary>The most of one message kept. Longer is cut.</summary>
    public const int MaxMessageLength = 8 * 1024;

    public const int MaxExceptionLength = 16 * 1024;

    /// <summary>The most property JSON kept, in UTF-8 bytes. More is stored as an empty object.</summary>
    public const int MaxPropertiesBytes = 16 * 1024;

    public long Id { get; set; }

    /// <summary>When Cloud received the batch. Cloud's clock, and the partition key.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Which deployment sent it: its id in the server registry.</summary>
    public Guid ServerId { get; set; }

    /// <summary>When the line was written, on the sending deployment's clock.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary><c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c> or <c>Fatal</c>.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The line with its values filled in. Somebody else's text: treat as hostile where it is shown.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The line before the values were filled in.</summary>
    public string? Template { get; set; }

    /// <summary>The class that wrote it.</summary>
    public string? Source { get; set; }

    /// <summary>Which stream it belongs to: <c>Sync</c>, <c>Moderation</c>, <c>Analytics</c>, <c>Setup</c>, <c>Discord</c>.</summary>
    public string? Area { get; set; }

    /// <summary>Which program wrote it, e.g. <c>Modbot</c>.</summary>
    public string? Service { get; set; }

    /// <summary>The deployment's release, e.g. <c>2026.9.0</c>.</summary>
    public string? Version { get; set; }

    public string? Exception { get; set; }

    /// <summary>jsonb: everything else the line carried. Never null.</summary>
    public string Properties { get; set; } = "{}";
}

internal sealed class ServerLogLineConfiguration : IEntityTypeConfiguration<ServerLogLine>
{
    public void Configure(EntityTypeBuilder<ServerLogLine> entity)
    {
        entity.ToTable("server_log");

        // PostgreSQL requires the partition key in every unique constraint on a partitioned table,
        // so the key is (id, received_at) rather than id alone -- the same shape modbot_event uses.
        entity.HasKey(e => new { e.Id, e.ReceivedAt }).HasName("pk_server_log");

        // A plain sequence default rather than an identity column: identity is not accepted on a
        // partitioned parent in PostgreSQL 16, which is the floor this targets.
        entity.Property(e => e.Id).UseSerialColumn();

        entity.Property(e => e.Level).HasMaxLength(ServerLogLine.MaxLevelLength);
        entity.Property(e => e.Source).HasMaxLength(ServerLogLine.MaxSourceLength);
        entity.Property(e => e.Area).HasMaxLength(ServerLogLine.MaxAreaLength);
        entity.Property(e => e.Service).HasMaxLength(ServerLogLine.MaxServiceLength);
        entity.Property(e => e.Version).HasMaxLength(ServerLogLine.MaxVersionLength);
        entity.Property(e => e.Properties).HasColumnType("jsonb");

        // One deployment's recent lines: the viewer's default question.
        entity.HasIndex(e => new { e.ServerId, e.ReceivedAt })
            .HasDatabaseName("ix_server_log_server_received_at")
            .IsDescending(false, true);

        // "Show me the errors across every deployment", which is the other one.
        entity.HasIndex(e => new { e.Level, e.ReceivedAt })
            .HasDatabaseName("ix_server_log_level_received_at")
            .IsDescending(false, true);
    }
}
