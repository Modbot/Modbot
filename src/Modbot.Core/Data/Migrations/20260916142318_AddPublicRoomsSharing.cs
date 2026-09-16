using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicRoomsSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "managed_group_banner_url",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "managed_group_icon_url",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "public_rooms_reported_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_rooms_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "public_rooms_server_id",
                table: "settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "share_public_rooms",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "managed_group_banner_url",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "managed_group_icon_url",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "public_rooms_reported_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "public_rooms_secret_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "public_rooms_server_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "share_public_rooms",
                table: "settings");
        }
    }
}
