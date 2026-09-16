using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Npgsql;

namespace Modbot.Demo;

/// <summary>Holds the group the demo was built from, so both halves of the seeding agree.</summary>
public sealed class DemoPlanHolder
{
    public DemoPlan? Plan { get; set; }
}

/// <summary>
/// The one place demo mode is turned on, and the only thing that may turn it on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Demo mode's whole security model is that every visitor is an administrator</strong>
/// (demo mode design §3). Nothing checks a password, nothing checks a permission, and anybody who
/// can open the URL can do anything Modbot can do. That is fine for a demo full of made-up people
/// and catastrophic for a deployment holding real ones — so the switch is deliberately narrow:
/// </para>
/// <list type="number">
///   <item><c>MODBOT_DEMO</c> must be set, and</item>
///   <item>the database must never have been set up for real — no staff account, no finished
///   wizard — unless it is already a demo database, which only the demo seeder ever marks.</item>
/// </list>
/// <para>
/// <see cref="DecideAsync"/> runs once, before the container is built, because what the host
/// composes depends on the answer: a demo registers no VRChat sync, no Discord bot and no mail
/// relay at all. It reads the database directly rather than through Entity Framework because the
/// migrations have not run yet — on a fresh deployment the tables it asks about do not exist, and
/// "they do not exist" is itself the answer.
/// </para>
/// <para>
/// <see cref="DemoMode.IsOn"/> throws if anything asks before that, so a new caller cannot
/// accidentally read "off" during startup and act on it.
/// </para>
/// </remarks>
public static class DemoStartup
{
    /// <summary>
    /// Works out whether this process is a demo, and says so in one sentence for the startup log.
    /// </summary>
    public static async Task<string> DecideAsync(
        DemoMode demo,
        string connectionString,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demo);

        var (hasStaffAccount, onboardingComplete, holdsDemoData) = demo.Requested
            ? await ReadAsync(connectionString, ct)
            : (false, false, false);

        demo.Decide(hasStaffAccount, onboardingComplete, holdsDemoData);

        return demo.Explain(hasStaffAccount, onboardingComplete, holdsDemoData);
    }

    /// <summary>
    /// Fills in the quick half of the demo's data, if it is not there already.
    /// </summary>
    /// <remarks>Called after the migrations, and before the first request is served.</remarks>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();

        var demo = scope.ServiceProvider.GetRequiredService<DemoMode>();

        if (!demo.IsOn)
            return;

        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();

        if (await seeder.IsSeededAsync(ct))
            return;

        var holder = scope.ServiceProvider.GetRequiredService<DemoPlanHolder>();
        holder.Plan = await seeder.SeedCoreAsync(ct);
    }

    /// <summary>
    /// What the database says about itself: a staff account, a finished wizard, demo data.
    /// </summary>
    /// <remarks>
    /// A database whose tables are not there yet has none of the three, which is exactly right: a
    /// deployment that has never run a migration has certainly never been set up.
    /// </remarks>
    private static async Task<(bool Staff, bool Onboarded, bool DemoData)> ReadAsync(
        string connectionString,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await OpenAsync(connection, ct);

        var staff = false;
        var onboarded = false;
        var demoData = false;

        if (await TableExistsAsync(connection, "modbot_user", ct))
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM modbot_user)", connection);
            staff = await command.ExecuteScalarAsync(ct) is true;
        }

        if (await TableExistsAsync(connection, "settings", ct))
        {
            // demo_data arrived with demo mode, so a database from an earlier version does not
            // have the column. Asking for it unconditionally would turn an upgrade into a crash.
            var hasDemoData = await ColumnExistsAsync(connection, "settings", "demo_data", ct);

            var sql = hasDemoData
                ? "SELECT onboarding_complete, demo_data FROM settings WHERE id = 1"
                : "SELECT onboarding_complete, false FROM settings WHERE id = 1";

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                onboarded = reader.GetBoolean(0);
                demoData = reader.GetBoolean(1);
            }
        }

        return (staff, onboarded, demoData);
    }

    /// <summary>
    /// Opens the connection, waiting for a database that is not up yet.
    /// </summary>
    /// <remarks>
    /// This read happens before the migrations, and the migrator already waits for a database a
    /// hosting platform has not finished starting. Without the same patience here, a demo on such a
    /// platform would crash-loop on the one boot where the database is thirty seconds behind it.
    /// </remarks>
    private static async Task OpenAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(ct);
                return;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", "public." + table);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection, string table, string column, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table AND column_name = @column)
            """,
            connection);

        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
