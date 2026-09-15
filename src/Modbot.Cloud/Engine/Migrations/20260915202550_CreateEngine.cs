using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
{
    /// <inheritdoc />
    public partial class CreateEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_event",
                columns: table => new
                {
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_event_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clock_adjustment_ms = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type_raw = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    subject_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    world_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    client_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_event", x => new { x.install_id, x.client_event_id });
                });

            migrationBuilder.CreateTable(
                name: "event_day_total",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    events = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_day_total", x => new { x.day, x.install_id });
                });

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

            migrationBuilder.CreateIndex(
                name: "ix_client_event_install_id_received_at",
                table: "client_event",
                columns: new[] { "install_id", "received_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_client_event_received_at",
                table: "client_event",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_client_event_type_occurred_at",
                table: "client_event",
                columns: new[] { "type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_event_day_total_install_id_day",
                table: "event_day_total",
                columns: new[] { "install_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_install_clock_updated_at",
                table: "install_clock",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_event");

            migrationBuilder.DropTable(
                name: "event_day_total");

            migrationBuilder.DropTable(
                name: "event_hour_total");

            migrationBuilder.DropTable(
                name: "install_clock");
        }
    }
}
