using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemindStaffAboutTrialsAndQuietAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "staff_trial",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "council_reminded_at",
                table: "staff_trial",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "staff_reminders_channel_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "check_in_reminded_at",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "staff_trial");

            migrationBuilder.DropColumn(
                name: "council_reminded_at",
                table: "staff_trial");

            migrationBuilder.DropColumn(
                name: "staff_reminders_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "check_in_reminded_at",
                table: "modbot_user");
        }
    }
}
