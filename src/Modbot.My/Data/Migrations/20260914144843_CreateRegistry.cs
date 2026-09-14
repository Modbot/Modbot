using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.My.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "register_page_instance",
                columns: table => new
                {
                    instance_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_register_page_instance", x => x.instance_url);
                });

            migrationBuilder.CreateTable(
                name: "registered_instance",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    analytics_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_usage_report_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scale_bucket = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    paired_clients = table.Column<int>(type: "integer", nullable: true),
                    discord_connected = table.Column<bool>(type: "boolean", nullable: true),
                    term_lists_imported = table.Column<List<string>>(type: "text[]", nullable: true),
                    rate_limit_cold_stops = table.Column<int>(type: "integer", nullable: true),
                    waf_blocks = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registered_instance", x => x.instance_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_register_page_instance_last_seen_at",
                table: "register_page_instance",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_registered_instance_instance_url",
                table: "registered_instance",
                column: "instance_url");

            migrationBuilder.CreateIndex(
                name: "ix_registered_instance_last_seen_at",
                table: "registered_instance",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "register_page_instance");

            migrationBuilder.DropTable(
                name: "registered_instance");
        }
    }
}
