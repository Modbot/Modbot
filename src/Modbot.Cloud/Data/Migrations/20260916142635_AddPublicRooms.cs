using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "public_room",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    world_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    world_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    world_image_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    join_link = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    region = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_public_room", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rooms_server",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    group_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    group_icon_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    group_banner_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    first_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rooms_server", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_public_room_server_id_location",
                table: "public_room",
                columns: new[] { "server_id", "location" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rooms_server_group_id",
                table: "rooms_server",
                column: "group_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rooms_server_last_reported_at",
                table: "rooms_server",
                column: "last_reported_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "public_room");

            migrationBuilder.DropTable(
                name: "rooms_server");
        }
    }
}
