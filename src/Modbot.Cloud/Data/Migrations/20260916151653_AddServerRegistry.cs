using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "page_instance",
                columns: table => new
                {
                    instance_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_instance", x => x.instance_url);
                });

            migrationBuilder.CreateTable(
                name: "registered_server",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_address = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    host_platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    group_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    group_icon_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    group_banner_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    discord_connected = table.Column<bool>(type: "boolean", nullable: true),
                    term_lists_imported = table.Column<List<string>>(type: "text[]", nullable: true),
                    rate_limit_cold_stops = table.Column<int>(type: "integer", nullable: true),
                    waf_blocks = table.Column<int>(type: "integer", nullable: true),
                    ai_moderation_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_report_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    link_code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    link_code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registered_server", x => x.id);
                    table.ForeignKey(
                        name: "fk_registered_server_account_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "visitor_instance",
                columns: table => new
                {
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    instance_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_visit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visitor_instance", x => new { x.ip_address, x.instance_url });
                });

            migrationBuilder.CreateTable(
                name: "server_report",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    public_address = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    host_platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    discord_connected = table.Column<bool>(type: "boolean", nullable: true),
                    term_lists_imported = table.Column<List<string>>(type: "text[]", nullable: true),
                    rate_limit_cold_stops = table.Column<int>(type: "integer", nullable: true),
                    waf_blocks = table.Column<int>(type: "integer", nullable: true),
                    ai_moderation_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_report", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_report_registered_server_server_id",
                        column: x => x.server_id,
                        principalTable: "registered_server",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_instance_last_seen_at",
                table: "page_instance",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_registered_server_account_id",
                table: "registered_server",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_registered_server_last_seen_at",
                table: "registered_server",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_registered_server_link_code_hash",
                table: "registered_server",
                column: "link_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_registered_server_public_address",
                table: "registered_server",
                column: "public_address");

            migrationBuilder.CreateIndex(
                name: "ix_server_report_server_id_reported_at",
                table: "server_report",
                columns: new[] { "server_id", "reported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_visitor_instance_instance_url",
                table: "visitor_instance",
                column: "instance_url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_instance");

            migrationBuilder.DropTable(
                name: "server_report");

            migrationBuilder.DropTable(
                name: "visitor_instance");

            migrationBuilder.DropTable(
                name: "registered_server");
        }
    }
}
