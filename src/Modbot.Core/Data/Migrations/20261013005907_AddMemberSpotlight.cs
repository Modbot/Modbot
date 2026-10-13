using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberSpotlight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "spotlight_day",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "spotlight_handled_through",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "spotlight_hour",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<string>(
                name: "spotlight_mode",
                table: "settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "random");

            migrationBuilder.AddColumn<bool>(
                name: "spotlight_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "spotlight_places",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "spotlight_time_zone",
                table: "settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "spotlight",
                table: "member_profile",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "spotlight_week",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week = table.Column<DateOnly>(type: "date", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: true),
                    post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    picked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spotlight_week", x => x.id);
                    table.ForeignKey(
                        name: "fk_spotlight_week_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_spotlight_week_post_id",
                table: "spotlight_week",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_spotlight_week_user",
                table: "spotlight_week",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_spotlight_week_week",
                table: "spotlight_week",
                column: "week",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spotlight_week");

            migrationBuilder.DropColumn(
                name: "spotlight_day",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_handled_through",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_hour",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_mode",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_places",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight_time_zone",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "spotlight",
                table: "member_profile");
        }
    }
}
