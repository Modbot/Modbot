using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>
/// Stores one checked batch in the event storage, in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dedupe.</strong> For each file the batch names, the <c>log_file</c> row is created if
/// new and locked. Lines at or below its <c>stored_through</c> are duplicates; the rest are stored in
/// offset order and the marker moves up (cloud log backup spec 4.4). A retry, a double send and a
/// replay after a restart all come out as duplicates, and two requests from one install wait on
/// the lock rather than both storing a line.
/// </para>
/// <para>
/// <strong>Everything or nothing.</strong> Lines, events, the file markers, the daily and hourly
/// totals and the install's clock are all written in the same transaction, so a total can never
/// count a line that was not stored.
/// </para>
/// <para>
/// Lines and events are written with PostgreSQL's binary <c>COPY</c>, which is many times faster
/// than row-by-row inserts at a thousand lines a batch.
/// </para>
/// </remarks>
public sealed class LogBatchWriter(EngineContext engine)
{
    public async Task<LogBatchResponse> WriteAsync(Guid installId, CheckedBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var clock = ClockCorrection.Read(receivedAt, batch.SentAt, batch.ClockOffsetMs, batch.ClockConfidence);

        await engine.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)engine.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var stored = new List<(long FileId, CheckedLine Line)>();

            foreach (var file in batch.Lines.GroupBy(l => l.File, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var (fileId, storedThrough) = await LockFileAsync(connection, installId, file.Key, receivedAt, ct);

                var fresh = file
                    .Where(l => l.Offset > storedThrough)
                    .DistinctBy(l => l.Offset)
                    .OrderBy(l => l.Offset)
                    .ToList();

                await AdvanceFileAsync(connection, fileId, fresh, receivedAt, ct);
                stored.AddRange(fresh.Select(l => (fileId, l)));
            }

            await CopyLinesAsync(connection, installId, batch, stored, receivedAt, ct);
            var hours = await CopyEventsAsync(connection, installId, batch, stored, clock, receivedAt, ct);
            await AddDayTotalAsync(connection, installId, stored.Count, receivedAt, ct);
            await AddHourTotalsAsync(connection, hours, ct);
            await SaveClockAsync(connection, installId, batch, clock, receivedAt, ct);

            await transaction.CommitAsync(ct);

            return new LogBatchResponse(stored.Count, batch.Lines.Count - stored.Count);
        }
        finally
        {
            await engine.Database.CloseConnectionAsync();
        }
    }

    private static async Task<(long Id, long StoredThrough)> LockFileAsync(
        NpgsqlConnection connection, Guid installId, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO log_file (install_id, name, stored_through, lines_stored, first_received_at, last_received_at)
            VALUES ($1, $2, -1, 0, $3, $3)
            ON CONFLICT (install_id, name) DO NOTHING
            """,
            connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = installId });
            insert.Parameters.Add(new NpgsqlParameter { Value = name });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var select = new NpgsqlCommand(
            "SELECT id, stored_through FROM log_file WHERE install_id = $1 AND name = $2 FOR UPDATE",
            connection);
        select.Parameters.Add(new NpgsqlParameter { Value = installId });
        select.Parameters.Add(new NpgsqlParameter { Value = name });

        await using var reader = await select.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task AdvanceFileAsync(
        NpgsqlConnection connection, long fileId, IReadOnlyList<CheckedLine> fresh, DateTimeOffset now, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE log_file
            SET stored_through = GREATEST(stored_through, $2),
                lines_stored = lines_stored + $3,
                last_received_at = $4
            WHERE id = $1
            """,
            connection);
        update.Parameters.Add(new NpgsqlParameter { Value = fileId });
        update.Parameters.Add(new NpgsqlParameter { Value = fresh.Count > 0 ? fresh[^1].Offset : -1L });
        update.Parameters.Add(new NpgsqlParameter { Value = (long)fresh.Count });
        update.Parameters.Add(new NpgsqlParameter { Value = now });
        await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopyLinesAsync(
        NpgsqlConnection connection,
        Guid installId,
        CheckedBatch batch,
        IReadOnlyList<(long FileId, CheckedLine Line)> stored,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        if (stored.Count == 0)
            return;

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY log_line (received_at, sent_at, logged_at, utc_offset_minutes, install_id, log_file_id, line_offset, text) FROM STDIN (FORMAT BINARY)",
            ct);

        foreach (var (fileId, line) in stored)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(receivedAt, NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(batch.SentAt, NpgsqlDbType.TimestampTz, ct);

            if (line.LoggedAt is { } logged)
                await writer.WriteAsync(logged, NpgsqlDbType.Timestamp, ct);
            else
                await writer.WriteNullAsync(ct);

            if (line.UtcOffsetMinutes is { } minutes)
                await writer.WriteAsync(minutes, NpgsqlDbType.Smallint, ct);
            else
                await writer.WriteNullAsync(ct);

            await writer.WriteAsync(installId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(fileId, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(line.Offset, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(line.Text, NpgsqlDbType.Text, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static async Task<Dictionary<(string Type, DateTimeOffset Hour), long>> CopyEventsAsync(
        NpgsqlConnection connection,
        Guid installId,
        CheckedBatch batch,
        IReadOnlyList<(long FileId, CheckedLine Line)> stored,
        ClockReading clock,
        DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        var hours = new Dictionary<(string, DateTimeOffset), long>();
        var events = stored.Where(s => s.Line.Event is not null).ToList();
        if (events.Count == 0)
            return hours;

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY log_event (received_at, occurred_at, install_id, log_file_id, line_offset, type, type_raw, parsed_by, data) FROM STDIN (FORMAT BINARY)",
            ct);

        foreach (var (fileId, line) in events)
        {
            var logEvent = line.Event!;
            var occurredAt = ClockCorrection.OccurredAt(line.LoggedAt, line.UtcOffsetMinutes, clock.Applied);

            await writer.StartRowAsync(ct);
            await writer.WriteAsync(receivedAt, NpgsqlDbType.TimestampTz, ct);

            if (occurredAt is { } occurred)
                await writer.WriteAsync(occurred, NpgsqlDbType.TimestampTz, ct);
            else
                await writer.WriteNullAsync(ct);

            await writer.WriteAsync(installId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(fileId, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(line.Offset, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(logEvent.Type, NpgsqlDbType.Varchar, ct);

            if (logEvent.TypeRaw is { } raw)
                await writer.WriteAsync(raw, NpgsqlDbType.Varchar, ct);
            else
                await writer.WriteNullAsync(ct);

            await writer.WriteAsync(batch.ClientVersion, NpgsqlDbType.Varchar, ct);
            await writer.WriteAsync(logEvent.Data, NpgsqlDbType.Jsonb, ct);

            var hour = Hour(occurredAt ?? receivedAt);
            hours[(logEvent.Type, hour)] = hours.GetValueOrDefault((logEvent.Type, hour)) + 1;
        }

        await writer.CompleteAsync(ct);
        return hours;
    }

    private static async Task AddDayTotalAsync(
        NpgsqlConnection connection, Guid installId, int lines, DateTimeOffset receivedAt, CancellationToken ct)
    {
        if (lines == 0)
            return;

        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO line_day_total (day, install_id, lines) VALUES ($1, $2, $3)
            ON CONFLICT (day, install_id) DO UPDATE SET lines = line_day_total.lines + excluded.lines
            """,
            connection);
        upsert.Parameters.Add(new NpgsqlParameter { Value = DateOnly.FromDateTime(receivedAt.UtcDateTime) });
        upsert.Parameters.Add(new NpgsqlParameter { Value = installId });
        upsert.Parameters.Add(new NpgsqlParameter { Value = (long)lines });
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddHourTotalsAsync(
        NpgsqlConnection connection, Dictionary<(string Type, DateTimeOffset Hour), long> hours, CancellationToken ct)
    {
        if (hours.Count == 0)
            return;

        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO event_hour_total (type, hour, events)
            SELECT * FROM unnest($1::varchar[], $2::timestamptz[], $3::bigint[])
            ON CONFLICT (type, hour) DO UPDATE SET events = event_hour_total.events + excluded.events
            """,
            connection);
        upsert.Parameters.Add(new NpgsqlParameter { Value = hours.Keys.Select(k => k.Type).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter { Value = hours.Keys.Select(k => k.Hour).ToArray() });
        upsert.Parameters.Add(new NpgsqlParameter { Value = hours.Values.ToArray() });
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static async Task SaveClockAsync(
        NpgsqlConnection connection, Guid installId, CheckedBatch batch, ClockReading clock, DateTimeOffset now, CancellationToken ct)
    {
        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO install_clock
                (install_id, reported_offset_ms, reported_confidence, observed_offset_ms, applied_offset_ms, disagrees, batches, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, 1, $7)
            ON CONFLICT (install_id) DO UPDATE SET
                reported_offset_ms = excluded.reported_offset_ms,
                reported_confidence = excluded.reported_confidence,
                observed_offset_ms = excluded.observed_offset_ms,
                applied_offset_ms = excluded.applied_offset_ms,
                disagrees = excluded.disagrees,
                batches = install_clock.batches + 1,
                updated_at = excluded.updated_at
            """,
            connection);
        upsert.Parameters.Add(new NpgsqlParameter { Value = installId });
        upsert.Parameters.Add(new NpgsqlParameter { Value = batch.ClockOffsetMs ?? 0L });
        upsert.Parameters.Add(new NpgsqlParameter { Value = batch.ClockConfidence });
        upsert.Parameters.Add(new NpgsqlParameter { Value = (long)clock.Observed.TotalMilliseconds });
        upsert.Parameters.Add(new NpgsqlParameter { Value = (long)clock.Applied.TotalMilliseconds });
        upsert.Parameters.Add(new NpgsqlParameter { Value = clock.Disagrees });
        upsert.Parameters.Add(new NpgsqlParameter { Value = now });
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static DateTimeOffset Hour(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
