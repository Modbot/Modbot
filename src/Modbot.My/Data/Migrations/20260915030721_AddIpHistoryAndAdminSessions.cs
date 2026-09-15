using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.My.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIpHistoryAndAdminSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ip_address",
                table: "registered_instance",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

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
                name: "registered_instance_ip",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registered_instance_ip", x => new { x.instance_id, x.ip_address });
                    table.ForeignKey(
                        name: "fk_registered_instance_ip_registered_instance_instance_id",
                        column: x => x.instance_id,
                        principalTable: "registered_instance",
                        principalColumn: "instance_id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateIndex(
                name: "ix_admin_session_expires_at",
                table: "admin_session",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_visitor_instance_instance_url",
                table: "visitor_instance",
                column: "instance_url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_session");

            migrationBuilder.DropTable(
                name: "registered_instance_ip");

            migrationBuilder.DropTable(
                name: "visitor_instance");

            migrationBuilder.DropColumn(
                name: "ip_address",
                table: "registered_instance");
        }
    }
}
