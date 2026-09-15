using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// The migration that turns an existing moderation log channel into a route (Discord event routes
/// design §8): run against a database at the migration before it, with the old columns filled in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventRouteMigrationTests
{
    private const string Routes = "20260915064650_AddDiscordEventRoutes";

    private readonly PostgresFixture _db;

    public EventRouteMigrationTests(PostgresFixture db) => _db = db;

    /// <summary>The migration just before routes, looked up so a migration merged in between does not break the test.</summary>
    private static string Before(IsolatedDatabase database)
    {
        using var context = database.NewContext();
        var all = context.Database.GetMigrations().ToList();
        return all[all.IndexOf(Routes) - 1];
    }

    [Fact]
    public async Task AnUntouchedChoice_BecomesARouteWithEveryModerationType_AndKeepsItsPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(" 1234567890 ", null, 4711, ct);

        await MigrateToAsync(database, Routes, ct);

        await using var db = database.NewContext();
        var route = Assert.Single(await db.DiscordEventRoutes.AsNoTracking().ToListAsync(ct));
        Assert.Equal("1234567890", route.ChannelId);
        Assert.True(route.Enabled);
        Assert.Null(route.Name);
        Assert.Equal(ModerationLogEvents.Allowed, route.EventTypes);
        Assert.Empty(route.SubjectIds);
        Assert.Empty(route.ActorIds);
        Assert.False(route.ActorAutomatic);
        Assert.Empty(route.SubjectVRChatRoleIds);
        Assert.Empty(route.ActorVRChatRoleIds);
        Assert.Empty(route.ActorModbotRoleIds);

        var place = Assert.Single(await db.DiscordEventChannels.AsNoTracking().ToListAsync(ct));
        Assert.Equal("1234567890", place.ChannelId);
        Assert.Equal(4711, place.PostedThrough);
        Assert.Null(place.LastError);
    }

    [Fact]
    public async Task AChosenList_KeepsOnlyTheOldListsTypes_InItsOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(
            "1234567890",
            $"{FactType.RoleRevoked}, {FactType.Login},{FactType.MemberBanned},,nonsense",
            null,
            ct);

        await MigrateToAsync(database, Routes, ct);

        await using var db = database.NewContext();
        var route = Assert.Single(await db.DiscordEventRoutes.AsNoTracking().ToListAsync(ct));
        Assert.Equal([FactType.MemberBanned, FactType.RoleRevoked], route.EventTypes);

        // It had not started, so it has no place and starts from the newest fact, as before.
        Assert.Empty(await db.DiscordEventChannels.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task AnEmptyChoice_BecomesARouteThatSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync("1234567890", string.Empty, 12, ct);

        await MigrateToAsync(database, Routes, ct);

        await using var db = database.NewContext();
        var route = Assert.Single(await db.DiscordEventRoutes.AsNoTracking().ToListAsync(ct));
        Assert.Empty(route.EventTypes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task NoLogChannel_MakesNoRoute(string? channel)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync(channel, null, 99, ct);

        await MigrateToAsync(database, Routes, ct);

        await using var db = database.NewContext();
        Assert.Empty(await db.DiscordEventRoutes.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.DiscordEventChannels.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task GoingBack_PutsTheFirstRouteBackAsTheLogChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await OldDatabaseAsync("1234567890", $"{FactType.MemberBanned},{FactType.MemberKicked}", 55, ct);

        await MigrateToAsync(database, Routes, ct);
        await MigrateToAsync(database, Before(database), ct);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var read = new NpgsqlCommand(
            "SELECT discord_log_channel_id, discord_log_event_types, discord_log_posted_through FROM settings WHERE id = 1",
            connection);
        await using var reader = await read.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("1234567890", reader.GetString(0));
        Assert.Equal($"{FactType.MemberBanned},{FactType.MemberKicked}", reader.GetString(1));
        Assert.Equal(55, reader.GetInt64(2));
    }

    /// <summary>A database at the migration before routes, with a settings row holding the old log channel columns.</summary>
    private async Task<IsolatedDatabase> OldDatabaseAsync(string? channel, string? types, long? postedThrough, CancellationToken ct)
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
            $"""
            INSERT INTO settings (id, discord_log_channel_id, discord_log_event_types, discord_log_posted_through{names})
            VALUES (1, @channel, @types, @through{values})
            """,
            connection);
        insert.Parameters.AddWithValue("channel", (object?)channel ?? DBNull.Value);
        insert.Parameters.AddWithValue("types", (object?)types ?? DBNull.Value);
        insert.Parameters.AddWithValue("through", (object?)postedThrough ?? DBNull.Value);
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

    private static async Task MigrateToAsync(IsolatedDatabase database, string migration, CancellationToken ct)
    {
        await using var context = database.NewContext();
        await context.GetService<IMigrator>().MigrateAsync(migration, ct);
    }
}
