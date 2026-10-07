using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// The migration that moves "Members can use /me" into the <c>discord_commands</c> setting (Discord
/// commands design §3.8): run against a database at the migration before it, with the old column
/// filled in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CommandSwitchMigrationTests
{
    private const string Moved = "20261007073547_MoveMeIntoCommandSwitches";

    private readonly PostgresFixture _db;

    public CommandSwitchMigrationTests(PostgresFixture db) => _db = db;

    /// <summary>The migration just before this one, looked up so a migration merged in between does not break the test.</summary>
    private static string Before(IsolatedDatabase database)
    {
        using var context = database.NewContext();
        var all = context.Database.GetMigrations().ToList();
        return all[all.IndexOf(Moved) - 1];
    }

    [Fact]
    public async Task AMeThatWasOn_StaysOn_AndEveryOtherCommandKeepsItsDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(meOn: true, ct);

        await MigrateToAsync(database, null, ct);

        await using var db = database.NewContext();
        var stored = (await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).DiscordCommands;

        Assert.True(DiscordCommandSwitches.IsOn(stored, "me"));
        Assert.Equal("{\"me\": true}", stored);
        Assert.All(
            DiscordCommandSwitches.All.Where(c => c.Name != "me"),
            c => Assert.Equal(c.OnByDefault, DiscordCommandSwitches.IsOn(stored, c.Name)));
    }

    [Fact]
    public async Task AMeThatWasOff_StaysOff_WithNothingStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(meOn: false, ct);

        await MigrateToAsync(database, null, ct);

        await using var db = database.NewContext();
        var stored = (await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).DiscordCommands;

        Assert.False(DiscordCommandSwitches.IsOn(stored, "me"));
        Assert.Empty(DiscordCommandSwitches.Read(stored));
    }

    [Fact]
    public async Task GoingBack_PutsTheSwitchBackWhereItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(meOn: true, ct);

        await MigrateToAsync(database, Moved, ct);
        await MigrateToAsync(database, Before(database), ct);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var read = new NpgsqlCommand("SELECT discord_me_command FROM settings WHERE id = 1", connection);
        Assert.True((bool)(await read.ExecuteScalarAsync(ct))!);
    }

    /// <summary>A database at the migration before this one, with a settings row whose <c>/me</c> switch is set.</summary>
    private async Task<IsolatedDatabase> OldDatabaseAsync(bool meOn, CancellationToken ct)
    {
        var database = await IsolatedDatabase.CreateAsync(_db, ct, migrate: false);
        await MigrateToAsync(database, Before(database), ct);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);

        // Every column the row cannot be without, filled with a plain value of its type, so the
        // test does not have to track the settings table's history.
        var required = new List<(string Name, string Type)>();
        await using (var columns = new NpgsqlCommand(
            """
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_name = 'settings' AND is_nullable = 'NO' AND column_default IS NULL AND column_name <> 'id'
            """,
            connection))
        await using (var reader = await columns.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                required.Add((reader.GetString(0), reader.GetString(1)));
        }

        var names = string.Join(string.Empty, required.Select(c => ", " + c.Name));
        var values = string.Join(string.Empty, required.Select(c => ", " + Blank(c.Type)));

        await using var insert = new NpgsqlCommand(
            $"INSERT INTO settings (id, discord_me_command{names}) VALUES (1, @me{values})",
            connection);
        insert.Parameters.AddWithValue("me", meOn);
        await insert.ExecuteNonQueryAsync(ct);

        return database;
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

    /// <summary>Migrates up or down to <paramref name="migration"/>, or to the newest when null.</summary>
    private static async Task MigrateToAsync(IsolatedDatabase database, string? migration, CancellationToken ct)
    {
        await using var context = database.NewContext();
        await context.GetService<IMigrator>().MigrateAsync(migration, ct);
    }
}
