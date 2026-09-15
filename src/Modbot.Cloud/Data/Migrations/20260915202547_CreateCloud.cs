using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateCloud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_session",
                columns: table => new
                {
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_session", x => x.token_hash);
                });

            migrationBuilder.CreateTable(
                name: "install",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    modbot_server_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_install", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    event_keep_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                    table.CheckConstraint("ck_settings_single_row", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_session_expires_at",
                table: "admin_session",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_install_last_seen_at",
                table: "install",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_session");

            migrationBuilder.DropTable(
                name: "install");

            migrationBuilder.DropTable(
                name: "settings");
        }
    }
}
