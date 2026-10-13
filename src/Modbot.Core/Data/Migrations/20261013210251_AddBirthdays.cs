using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBirthdays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "birthday_channel_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "birthday_handled_through",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "birthday_hour",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<string>(
                name: "birthday_message",
                table: "settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birthday_problem",
                table: "settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birthday_role_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birthday_time_zone",
                table: "settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "birthdays_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "member_birthday",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    role_given_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    role_given_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_birthday", x => x.discord_user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_birthday_day",
                table: "member_birthday",
                columns: new[] { "month", "day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_birthday");

            migrationBuilder.DropColumn(
                name: "birthday_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_handled_through",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_hour",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_message",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthday_time_zone",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "birthdays_on",
                table: "settings");
        }
    }
}
