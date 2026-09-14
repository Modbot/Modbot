using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "announcement_channel_id",
                table: "vrchat_instance",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "announcement_finished",
                table: "vrchat_instance",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "announcement_message_id",
                table: "vrchat_instance",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "announcement_updated_at",
                table: "vrchat_instance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_instance_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_instance_message",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_instance_announcing",
                table: "vrchat_instance",
                column: "announcement_updated_at",
                filter: "announcement_finished = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vrchat_instance_announcing",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "announcement_channel_id",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "announcement_finished",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "announcement_message_id",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "announcement_updated_at",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "discord_instance_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_instance_message",
                table: "settings");
        }
    }
}
