using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Cloud.Features.ServerLogs;

/// <param name="Stored">Lines written.</param>
public sealed record LogBatchResponse(int Stored);

/// <summary>
/// Stores one checked batch of log lines.
/// </summary>
/// <remarks>
/// <para>
/// One COPY, which is what PostgreSQL is fastest at and what keeps a deployment catching up after a
/// day offline from costing Cloud a thousand statements.
/// </para>
/// <para>
/// <strong>No de-duplication.</strong> Events are keyed on the client's own event id because a
/// retried batch must not store them twice; a log line has no such id, and giving it one would mean
/// a unique index over the whole table — which a partitioned table cannot hold without its partition
/// key, and which would cost more than the problem. Instead the sender advances its place-marker
/// only after Cloud has answered, so a retry re-sends the same lines only when the answer was lost.
/// A handful of repeated lines after a dropped connection is the right price for not indexing
/// hundreds of millions of rows.
/// </para>
/// </remarks>
public sealed class LogBatchWriter(EngineContext engine, LogPartitionMaintainer partitions)
{
    private const string Copy = """
        COPY server_log (received_at, server_id, at, level, message, template, source, area, service,
                           version, exception, properties)
        FROM STDIN (FORMAT BINARY)
        """;

    public async Task<LogBatchResponse> WriteAsync(
        Guid serverId, CheckedLogBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // The partition key is Cloud's own clock, so the month a batch lands in is always one the
        // maintainer has made or is about to. Making sure here as well costs one catalogue read and
        // means the first batch after a month boundary never fails.
        await partitions.EnsureAsync(ct).ConfigureAwait(false);

        await engine.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var connection = (NpgsqlConnection)engine.Database.GetDbConnection();

            await using var writer = await connection.BeginBinaryImportAsync(Copy, ct).ConfigureAwait(false);

            foreach (var line in batch.Lines)
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(receivedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await writer.WriteAsync(serverId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                await writer.WriteAsync(line.At, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await writer.WriteAsync(line.Level, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                await writer.WriteAsync(line.Message, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await Nullable(writer, line.Template, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await Nullable(writer, line.Source, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                await Nullable(writer, line.Area, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                await Nullable(writer, line.Service, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                await Nullable(writer, batch.ServerVersion, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                await Nullable(writer, line.Exception, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await writer.WriteAsync(line.Properties, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
            }

            await writer.CompleteAsync(ct).ConfigureAwait(false);

            return new LogBatchResponse(batch.Lines.Count);
        }
        finally
        {
            await engine.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task Nullable(
        NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null)
            await writer.WriteNullAsync(ct).ConfigureAwait(false);
        else
            await writer.WriteAsync(value, type, ct).ConfigureAwait(false);
    }
}
