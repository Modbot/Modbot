using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
{
    /// <summary>
    /// Creates the event storage: log files, the two partitioned tables, the clock record and the
    /// totals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>log_line</c> and <c>log_event</c> are hand-written SQL, because EF Core cannot express
    /// declarative partitioning. <c>EngineContext</c> describes exactly this shape, so the snapshot
    /// stays accurate and later migrations diff correctly — change the columns in both places.
    /// </para>
    /// <para>
    /// <strong>No partitions are created here.</strong> Which months exist depends on the clock, and a
    /// migration must not read one. <c>PartitionMaintainer</c> makes them before Cloud serves its
    /// first request.
    /// </para>
    /// <para>
    /// Indexes are made on the parent, so every partition made later has them too.
    /// </para>
    /// </remarks>
    public partial class CreateEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_hour_total",
                columns: table => new
                {
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    events = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_hour_total", x => new { x.type, x.hour });
                });

            migrationBuilder.CreateTable(
                name: "install_clock",
                columns: table => new
                {
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reported_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    reported_confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    applied_offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    disagrees = table.Column<bool>(type: "boolean", nullable: false),
                    batches = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_install_clock", x => x.install_id);
                });

            migrationBuilder.CreateTable(
                name: "line_day_total",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lines = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_day_total", x => new { x.day, x.install_id });
                });

            migrationBuilder.CreateTable(
                name: "log_file",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    stored_through = table.Column<long>(type: "bigint", nullable: false),
                    lines_stored = table.Column<long>(type: "bigint", nullable: false),
                    first_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_file", x => x.id);
                });

            // A plain sequence default rather than GENERATED AS IDENTITY: identity columns are not
            // accepted on a partitioned parent in PostgreSQL 16.
            migrationBuilder.Sql("CREATE SEQUENCE log_line_id_seq AS bigint;");
            migrationBuilder.Sql("""
                CREATE TABLE log_line (
                    id                 bigint                      NOT NULL DEFAULT nextval('log_line_id_seq'),
                    received_at        timestamp with time zone    NOT NULL,
                    sent_at            timestamp with time zone    NOT NULL,
                    logged_at          timestamp without time zone NULL,
                    utc_offset_minutes smallint                    NULL,
                    install_id         uuid                        NOT NULL,
                    log_file_id        bigint                      NOT NULL,
                    line_offset        bigint                      NOT NULL,
                    text               text                        NOT NULL,
                    CONSTRAINT pk_log_line PRIMARY KEY (id, received_at)
                ) PARTITION BY RANGE (received_at);
                """);
            migrationBuilder.Sql("ALTER SEQUENCE log_line_id_seq OWNED BY log_line.id;");

            migrationBuilder.Sql("CREATE SEQUENCE log_event_id_seq AS bigint;");
            migrationBuilder.Sql("""
                CREATE TABLE log_event (
                    id                 bigint                      NOT NULL DEFAULT nextval('log_event_id_seq'),
                    received_at        timestamp with time zone    NOT NULL,
                    occurred_at        timestamp with time zone    NULL,
                    install_id         uuid                        NOT NULL,
                    log_file_id        bigint                      NOT NULL,
                    line_offset        bigint                      NOT NULL,
                    type               character varying(128)      NOT NULL,
                    type_raw           character varying(128)      NULL,
                    parsed_by          character varying(32)       NOT NULL,
                    data               jsonb                       NOT NULL,
                    CONSTRAINT pk_log_event PRIMARY KEY (id, received_at)
                ) PARTITION BY RANGE (received_at);
                """);
            migrationBuilder.Sql("ALTER SEQUENCE log_event_id_seq OWNED BY log_event.id;");

            migrationBuilder.CreateIndex(
                name: "ix_install_clock_updated_at",
                table: "install_clock",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_line_day_total_install_id_day",
                table: "line_day_total",
                columns: new[] { "install_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_log_file_install_id_name",
                table: "log_file",
                columns: new[] { "install_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_log_file_last_received_at",
                table: "log_file",
                column: "last_received_at");

            // Per-install recent lines, for admin.
            migrationBuilder.Sql("CREATE INDEX ix_log_line_install_id_received_at ON log_line (install_id, received_at DESC);");

            // The trends index: events of one type across a range of hours.
            migrationBuilder.Sql("CREATE INDEX ix_log_event_type_occurred_at ON log_event (type, occurred_at);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_hour_total");

            migrationBuilder.DropTable(
                name: "install_clock");

            migrationBuilder.DropTable(
                name: "line_day_total");

            migrationBuilder.DropTable(
                name: "log_file");

            // CASCADE takes the partitions with it; they are not otherwise named here.
            migrationBuilder.Sql("DROP TABLE IF EXISTS log_event CASCADE;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS log_event_id_seq;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS log_line CASCADE;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS log_line_id_seq;");
        }
    }
}
