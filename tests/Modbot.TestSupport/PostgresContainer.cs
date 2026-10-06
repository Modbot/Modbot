using Testcontainers.PostgreSql;

namespace Modbot.TestSupport;

/// <summary>
/// How the test PostgreSQL container is configured, in the one place every suite starts it from.
/// </summary>
/// <remarks>
/// <para>
/// These databases are thrown away at the end of the run, so nothing here is for safety, only for
/// speed. Testcontainers already starts PostgreSQL with <c>fsync</c>, <c>full_page_writes</c> and
/// <c>synchronous_commit</c> off; this adds the rest:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The data directory is in memory (tmpfs), so no write reaches a disk. Memory is what it costs, and
/// a database nobody drops stays in it until the run ends, so a test that makes its own database
/// drops it when it is done (see <see cref="TestDatabases"/>). Set <c>MODBOT_TEST_POSTGRES_ON_DISK=1</c>
/// to keep the data on disk instead, for a machine with little memory.
/// </description></item>
/// <item><description>
/// <c>max_connections</c> is 500: a suite that runs several test classes at once has one connection
/// pool per live database, each of which may hold up to a hundred.
/// </description></item>
/// <item><description>
/// <c>shared_buffers</c> is 256 MB, double PostgreSQL's default, for the same reason.
/// </description></item>
/// <item><description>
/// <c>jit</c> is off. Compiling a query costs more than running one on tables this small.
/// </description></item>
/// </list>
/// </remarks>
internal static class PostgresContainer
{
    public const string Image = "postgres:16-alpine";

    private const string DataDirectory = "/var/lib/postgresql/data";

    public static PostgreSqlBuilder Builder()
    {
        // WithCommand adds to the command Testcontainers already set (the three settings above).
        var builder = new PostgreSqlBuilder(Image)
            .WithCommand("-c", "max_connections=500", "-c", "shared_buffers=256MB", "-c", "jit=off");

        if (Environment.GetEnvironmentVariable("MODBOT_TEST_POSTGRES_ON_DISK") != "1")
            builder = builder.WithTmpfsMount(DataDirectory);

        return builder;
    }
}
