using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modbot.Core.Data;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// Runs the multi-column statements the review jobs are made of.
/// </summary>
/// <remarks>
/// ADO rather than <c>SqlQueryRaw</c>, for the reason the analytics endpoints use it: these
/// statements project several columns and arrays, and EF's single-column shape does not fit.
/// Every value travels as a parameter; the only interpolated fragments are constants from this
/// assembly. Joins the ambient EF transaction when there is one, so a job that took an advisory
/// lock keeps holding it across these reads.
/// </remarks>
internal static class ReviewSql
{
    public static async Task<IReadOnlyList<T>> ReadAsync<T>(
        ModbotContext db,
        string sql,
        Func<DbDataReader, T> read,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = Prepare(db, connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<T>();
            while (await reader.ReadAsync(ct))
                rows.Add(read(reader));

            return rows;
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }

    public static async Task<int> ExecuteAsync(
        ModbotContext db,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = Prepare(db, connection, sql, parameters);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }

    private static DbCommand Prepare(
        ModbotContext db,
        DbConnection connection,
        string sql,
        (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public static DateTimeOffset InstantOf(DbDataReader reader, int ordinal)
        => reader.GetFieldValue<DateTimeOffset>(ordinal);

    public static DateTimeOffset? InstantOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    public static string? TextOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long[] LongsOf(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<long[]>(ordinal);

    /// <summary>The UTC day of an instant, as the fact log's partitions and the daily totals count days.</summary>
    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    /// <summary>UTC midnight beginning <paramref name="day"/>, as a <c>timestamptz</c> bound.</summary>
    public static DateTimeOffset DayStart(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
