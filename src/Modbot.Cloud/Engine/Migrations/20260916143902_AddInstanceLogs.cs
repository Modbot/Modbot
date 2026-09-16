using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
{
    /// <summary>
    /// The server log feed: the log lines Modbot deployments send Cloud for remote support and for
    /// an operator whose own container was thrown away (cloud event backup spec §0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written SQL rather than <c>CreateTable</c>, because EF Core cannot express declarative
    /// partitioning. The model in <c>InstanceLogLine</c> describes exactly this shape, so the
    /// snapshot stays accurate; if you change the columns here, change them there too.
    /// </para>
    /// <para>
    /// Partitioned by month on <c>received_at</c>, which is Cloud's own clock, so retention drops a
    /// whole month at a time. Not on <c>at</c>: that is the sending deployment's clock, and one
    /// whose clock is years out would write into a partition that does not exist and fail the whole
    /// batch. This is the one table in Cloud that is partitioned — the events beside it are keyed on
    /// the client's event id, which a partitioned table cannot hold as a unique key (spec §4.3), and
    /// there are far fewer of them.
    /// </para>
    /// <para>
    /// <strong>No partitions are created here.</strong> Which months exist depends on the clock, and
    /// a migration must not read one. <c>LogPartitionMaintainer</c> makes them, two months ahead of
    /// Cloud's own clock, at start and once a day.
    /// </para>
    /// </remarks>
    public partial class AddInstanceLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A plain sequence default rather than GENERATED AS IDENTITY: identity columns are not
            // accepted on a partitioned parent in PostgreSQL 16, which is the floor this targets.
            migrationBuilder.Sql("CREATE SEQUENCE instance_log_id_seq AS bigint;");

            migrationBuilder.Sql("""
                CREATE TABLE instance_log (
                    id          bigint      NOT NULL DEFAULT nextval('instance_log_id_seq'),
                    received_at timestamptz NOT NULL,
                    install_id  uuid        NOT NULL,
                    at          timestamptz NOT NULL,
                    level       varchar(16) NOT NULL,
                    message     text        NOT NULL,
                    template    text        NULL,
                    source      varchar(256) NULL,
                    area        varchar(32) NULL,
                    service     varchar(32) NULL,
                    version     varchar(32) NULL,
                    exception   text        NULL,
                    properties  jsonb       NOT NULL,
                    CONSTRAINT pk_instance_log PRIMARY KEY (id, received_at)
                ) PARTITION BY RANGE (received_at);
                """);

            migrationBuilder.Sql("ALTER SEQUENCE instance_log_id_seq OWNED BY instance_log.id;");

            // On the parent, so every month's partition inherits them -- including ones made years
            // from now by the maintenance job, without anyone remembering to.
            migrationBuilder.Sql("""
                CREATE INDEX ix_instance_log_install_received_at
                    ON instance_log (install_id, received_at DESC);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_instance_log_level_received_at
                    ON instance_log (level, received_at DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CASCADE takes the monthly partitions with it.
            migrationBuilder.Sql("DROP TABLE IF EXISTS instance_log CASCADE;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS instance_log_id_seq;");
        }
    }
}
