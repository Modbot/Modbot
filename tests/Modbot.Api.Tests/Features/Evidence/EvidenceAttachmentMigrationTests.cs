using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// The migration that moves "which case file holds this file" out of the file's own row into a row
/// per case file: run against a database at the migration before it, with files that were on case
/// files, one that named a case file that does not exist, and one that was on none.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceAttachmentMigrationTests(PostgresFixture db)
{
    private const string Migration = "EvidenceCanSitOnSeveralCaseFiles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Stored = new(2026, 9, 1, 8, 30, 0, TimeSpan.Zero);

    private static string Before(ModbotContext context)
    {
        var all = context.Database.GetMigrations().ToList();
        return all[all.FindIndex(m => m.EndsWith(Migration, StringComparison.Ordinal)) - 1];
    }

    private static string Named(ModbotContext context)
        => context.Database.GetMigrations().Single(m => m.EndsWith(Migration, StringComparison.Ordinal));

    /// <summary>A fresh database in the shared container, left unmigrated so the test can stop one short.</summary>
    private async Task<string> EmptyDatabaseAsync()
    {
        var name = $"modbot_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(db.ConnectionString))
        {
            await admin.OpenAsync(Ct);

            // A fresh GUID with a fixed prefix: nothing here is caller-supplied.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = name }.ConnectionString;
    }

    private static ModbotContext Open(string connectionString)
        => new(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(connectionString).Options);

    /// <summary>
    /// Inserts a row giving only the columns the test cares about. Every other column the row cannot
    /// be without is filled with a plain value of its type, so the test does not have to track the
    /// table's history.
    /// </summary>
    private static async Task InsertAsync(
        NpgsqlConnection connection, string table, Dictionary<string, object?> given)
    {
        var required = new List<(string Name, string Type)>();

        await using (var columns = new NpgsqlCommand(
            """
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_name = @table AND is_nullable = 'NO' AND column_default IS NULL
            """,
            connection))
        {
            columns.Parameters.AddWithValue("table", table);
            await using var reader = await columns.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                required.Add((reader.GetString(0), reader.GetString(1)));
        }

        var names = given.Keys.ToList();
        var values = names.Select(n => "@" + n).ToList();

        foreach (var (name, type) in required.Where(c => !given.ContainsKey(c.Name)))
        {
            names.Add(name);
            values.Add(Blank(type));
        }

        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})", connection);

        foreach (var (name, value) in given)
            insert.Parameters.AddWithValue(name, value ?? DBNull.Value);

        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static string Blank(string type) => type switch
    {
        "boolean" => "FALSE",
        "smallint" or "integer" or "bigint" or "numeric" or "double precision" or "real" => "0",
        "jsonb" or "json" => "'{}'",
        "uuid" => "'00000000-0000-0000-0000-000000000000'",
        "timestamp with time zone" => "'2026-09-13T12:00:00Z'",
        "interval" => "'0'",
        _ => "''",
    };

    private static Task BlobAsync(NpgsqlConnection connection, char hash, string? reportId, string? uploader)
        => InsertAsync(connection, "modbot_evidence_blob", new()
        {
            ["hash"] = new string(hash, 64),
            ["byte_size"] = 100L,
            ["content_type"] = "image/png",
            ["backend"] = (short)1,
            ["first_stored_at"] = Stored,
            ["file_name"] = $"{hash}.png",
            ["uploader_id"] = uploader,
            ["report_id"] = reportId,
            ["origin"] = (short)0,
        });

    [Fact]
    public async Task EveryFileOnACaseFileGetsAHold_AndTheRestGetNone()
    {
        var connectionString = await EmptyDatabaseAsync();
        var author = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        await using (var context = Open(connectionString))
            await context.GetService<IMigrator>().MigrateAsync(Before(context), Ct);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);

            await InsertAsync(connection, "modbot_user", new() { ["id"] = author, ["username"] = "Sam" });
            await InsertAsync(connection, "case_file", new()
            {
                ["id"] = caseId,
                ["user_id"] = "usr_banned",
                ["author_user_id"] = author,
                ["author_username"] = "Sam",
            });

            // On the case file, sent by an account (matched by username whatever its case).
            await BlobAsync(connection, 'a', caseId.ToString(), "sam");

            // On the case file, sent by somebody with no account any more.
            await BlobAsync(connection, 'b', caseId.ToString(), "gone-user");

            // Names a case file that does not exist, and one on no case file at all.
            await BlobAsync(connection, 'c', Guid.NewGuid().ToString(), "sam");
            await BlobAsync(connection, 'd', null, "sam");
        }

        await using (var context = Open(connectionString))
            await context.GetService<IMigrator>().MigrateAsync(Named(context), Ct);

        await using var after = Open(connectionString);
        var held = await after.EvidenceAttachments.AsNoTracking().OrderBy(a => a.Hash).ToListAsync(Ct);

        Assert.Equal([new string('a', 64), new string('b', 64)], held.Select(a => a.Hash));
        Assert.All(held, a =>
        {
            Assert.Equal(caseId.ToString(), a.CaseId);
            Assert.True(a.IsOn);
            Assert.Equal(Stored, a.AttachedAt);
        });

        Assert.Equal("a.png", held[0].FileName);
        Assert.Equal("sam", held[0].AttachedByName);
        Assert.Equal(author, held[0].AttachedByUserId);

        Assert.Equal("gone-user", held[1].AttachedByName);
        Assert.Null(held[1].AttachedByUserId);

        // Every file keeps its record, whether or not it was on a case file.
        Assert.Equal(4, await after.EvidenceBlobs.CountAsync(Ct));

        // And the column is gone, so nothing can read a stale single case file.
        await using var connection2 = new NpgsqlConnection(connectionString);
        await connection2.OpenAsync(Ct);
        await using var check = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'modbot_evidence_blob' AND column_name = 'report_id'",
            connection2);
        Assert.Equal(0L, await check.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task GoingBack_PutsTheCaseFileBackOnTheFile()
    {
        var connectionString = await EmptyDatabaseAsync();
        var author = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        await using (var context = Open(connectionString))
            await context.GetService<IMigrator>().MigrateAsync(Before(context), Ct);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);

            await InsertAsync(connection, "case_file", new()
            {
                ["id"] = caseId,
                ["user_id"] = "usr_banned",
                ["author_user_id"] = author,
                ["author_username"] = "Sam",
            });

            await BlobAsync(connection, 'e', caseId.ToString(), "sam");
        }

        string before;
        await using (var context = Open(connectionString))
        {
            before = Before(context);
            await context.GetService<IMigrator>().MigrateAsync(Named(context), Ct);
            await context.GetService<IMigrator>().MigrateAsync(before, Ct);
        }

        await using var read = new NpgsqlConnection(connectionString);
        await read.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            $"SELECT report_id FROM modbot_evidence_blob WHERE hash = '{new string('e', 64)}'", read);

        Assert.Equal(caseId.ToString(), await query.ExecuteScalarAsync(Ct));
    }
}
