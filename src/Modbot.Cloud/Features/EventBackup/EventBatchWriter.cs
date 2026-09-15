using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Cloud.Features.EventBackup;

/// <summary>
/// Stores one checked batch in the event storage, in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>De-duplication.</strong> Events are inserted with <c>ON CONFLICT DO NOTHING</c> on
/// <c>(install_id, client_event_id)</c>, and only the rows actually inserted come back. A client keeps
/// an event's id through every retry and restart, so a batch sent twice, or an outbox that closed the
/// same events twice after a crash, stores each event once (cloud event backup spec 4.3).
/// </para>
/// <para>
/// <strong>Everything or nothing.</strong> Events, the daily and hourly totals and the install's clock
/// are written in the same transaction, and the totals count only the rows that came back, so a total
/// never counts a duplicate.
/// </para>
/// </remarks>
public sealed class EventBatchWriter(EngineContext engine)
{
    private const string Insert = """
        INSERT INTO client_event (install_id, client_event_id, received_at, sent_at, occurred_at, occurred_before,
                                  clock_adjustment_ms, type, type_raw, subject_id, world_id, instance_id, group_id,
                                  client_version, data)
        SELECT $1, e.id, $2, $3, e.occurred_at, e.occurred_before, $4, e.type, e.type_raw, e.subject_id, e.world_id,
               e.instance_id, e.group_id, $5, e.data::jsonb
        FROM unnest($6::varchar[], $7::timestamptz[], $8::timestamptz[], $9::varchar[], $10::varchar[],
                    $11::varchar[], $12::varchar[], $13::varchar[], $14::varchar[], $15::text[])
             AS e(id, occurred_at, occurred_before, type, type_raw, subject_id, world_id, instance_id, group_id, data)
        ON CONFLICT (install_id, client_event_id) DO NOTHING
        RETURNING type, occurred_at
        """;

    public async Task<EventBatchResponse> WriteAsync(Guid installId, CheckedBatch batch, DateTimeOffset receivedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var clock = ClockCorrection.Read(receivedAt, batch.SentAt, batch.ClockOffsetMs, batch.ClockConfidence);
        var adjustmentMs = (int)Math.Clamp(clock.Adjustment.TotalMilliseconds, int.MinValue, int.MaxValue);
        var adjustment = TimeSpan.FromMilliseconds(adjustmentMs);
        var events = batch.Events.DistinctBy(e => e.ClientEventId, StringComparer.Ordinal).ToList();

        await engine.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)engine.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var hours = new Dictionary<(string Type, DateTimeOffset Hour), long>();
            var stored = 0;

            await using (var insert = new NpgsqlCommand(Insert, connection))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = installId });
                insert.Parameters.Add(new NpgsqlParameter { Value = receivedAt });
                insert.Parameters.Add(new NpgsqlParameter { Value = batch.SentAt });
                insert.Parameters.Add(new NpgsqlParameter { Value = adjustmentMs });
                insert.Parameters.Add(new NpgsqlParameter { Value = batch.ClientVersion });
                insert.Parameters.Add(Array(events.Select(e => e.ClientEventId)));
                insert.Parameters.Add(new NpgsqlParameter { Value = events.Select(e => e.OccurredAt + adjustment).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter
                {
                    Value = events.Select(e => e.OccurredBefore is { } before ? (object)(before + adjustment) : DBNull.Value).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                });
                insert.Parameters.Add(Array(events.Select(e => e.Type)));
                insert.Parameters.Add(Array(events.Select(e => e.TypeRaw)));
                insert.Parameters.Add(Array(events.Select(e => e.SubjectId)));
                insert.Parameters.Add(Array(events.Select(e => e.WorldId)));
                insert.Parameters.Add(Array(events.Select(e => e.InstanceId)));
                insert.Parameters.Add(Array(events.Select(e => e.GroupId)));
                insert.Parameters.Add(new NpgsqlParameter
                {
                    Value = events.Select(e => e.Data).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });

                await using var reader = await insert.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    stored++;
                    var key = (reader.GetString(0), Hour(reader.GetFieldValue<DateTimeOffset>(1)));
                    hours[key] = hours.GetValueOrDefault(key) + 1;
                }
            }

            await AddDayTotalAsync(connection, installId, stored, receivedAt, ct);
            await AddHourTotalsAsync(connection, hours, ct);
            await SaveClockAsync(connection, installId, batch, clock, receivedAt, ct);

            await transaction.CommitAsync(ct);

            return new EventBatchResponse(stored, batch.Events.Count - stored);
        }
        finally
        {
            await engine.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlParameter Array(IEnumerable<string?> values) => new()
    {
        Value = values.Select(v => v is null ? (object)DBNull.Value : v).ToArray(),
        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Varchar,
    };

    private static async Task AddDayTotalAsync(NpgsqlConnection connection, Guid installId, int events, DateTimeOffset receivedAt, CancellationToken ct)
    {
        if (events == 0)
            return;

        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO event_day_total (day, install_id, events) VALUES ($1, $2, $3)
            ON CONFLICT (day, install_id) DO UPDATE SET events = event_day_total.events + excluded.events
            """,
            connection);
        upsert.Parameters.Add(new NpgsqlParameter { Value = DateOnly.FromDateTime(receivedAt.UtcDateTime) });
        upsert.Parameters.Add(new NpgsqlParameter { Value = installId });
        upsert.Parameters.Add(new NpgsqlParameter { Value = (long)events });
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddHourTotalsAsync(NpgsqlConnection connection, Dictionary<(string Type, DateTimeOffset Hour), long> hours, CancellationToken ct)
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
