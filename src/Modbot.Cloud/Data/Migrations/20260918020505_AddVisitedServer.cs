using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitedServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visited_server",
                columns: table => new
                {
                    instance_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    group_icon_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    group_banner_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    owner_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visited_server", x => x.instance_url);
                });

            migrationBuilder.CreateIndex(
                name: "ix_visited_server_last_seen_at",
                table: "visited_server",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visited_server");
        }
    }
}
