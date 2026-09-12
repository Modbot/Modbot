using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Creates the fact log as a range-partitioned table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written SQL rather than <c>migrationBuilder.CreateTable</c> because EF Core cannot
    /// express declarative partitioning. The model in <c>ModbotContext</c> describes exactly this
    /// shape, so the snapshot stays accurate and later migrations diff correctly -- if you change
    /// the columns here, change them there too.
    /// </para>
    /// <para>
    /// Partitioning is not a performance flourish: spec 5.5 prunes presence facts at 90 days, and
    /// at peak volume that is tens of millions of rows. Dropping a partition is instant; a mass
    /// DELETE of the same rows is hours of bloat and vacuum.
    /// </para>
    /// <para>
    /// <strong>This table has no partitions yet.</strong> An insert with no matching partition
    /// fails, so <c>EventPartitionMaintainer</c> has to run before ingest and keep running. The
    /// migration deliberately creates none: which months exist depends on the clock, and a
    /// migration must not read one.
    /// </para>
    /// </remarks>
    public partial class AddFactLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A plain sequence default rather than GENERATED AS IDENTITY: identity columns are
            // not accepted on a partitioned parent in PostgreSQL 16, which is the floor Modbot
            // targets. This is what bigserial expands to anyway.
            migrationBuilder.Sql("CREATE SEQUENCE modbot_event_id_seq AS bigint;");

            migrationBuilder.Sql("""
                CREATE TABLE modbot_event (
                    id               bigint      NOT NULL DEFAULT nextval('modbot_event_id_seq'),
                    occurred_at      timestamptz NOT NULL,
                    occurred_before  timestamptz NULL,
                    observed_at      timestamptz NOT NULL,
                    type             smallint    NOT NULL,
                    subject_platform smallint    NOT NULL,
                    subject_id       text        NOT NULL,
                    actor_platform   smallint    NULL,
                    actor_id         text        NULL,
                    world_id         text        NULL,
                    instance_id      text        NULL,
                    source           smallint    NOT NULL,
                    data             jsonb       NOT NULL,
                    CONSTRAINT pk_modbot_event PRIMARY KEY (id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);
                """);

            migrationBuilder.Sql(
                "ALTER SEQUENCE modbot_event_id_seq OWNED BY modbot_event.id;");

            // Created on the parent, so every partition -- including ones created years from now
            // by the maintenance job -- inherits them without anyone remembering to.
            migrationBuilder.Sql("""
                CREATE INDEX ix_modbot_event_subject
                    ON modbot_event (subject_platform, subject_id, occurred_at DESC);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_modbot_event_actor
                    ON modbot_event (actor_platform, actor_id, occurred_at DESC);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_modbot_event_type
                    ON modbot_event (type, occurred_at DESC);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_modbot_event_dedup
                    ON modbot_event (instance_id, subject_id, type, occurred_at);
                """);

            migrationBuilder.Sql("CREATE INDEX ix_modbot_event_data ON modbot_event USING gin (data);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CASCADE takes the partitions with it; they are not otherwise named here.
            migrationBuilder.Sql("DROP TABLE IF EXISTS modbot_event CASCADE;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS modbot_event_id_seq;");
        }
    }
}
