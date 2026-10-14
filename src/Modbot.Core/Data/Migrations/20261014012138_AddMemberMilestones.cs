using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberMilestones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "milestone_back_days",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<string>(
                name: "milestone_channel_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "milestone_first_at",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "milestone_post_back",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "milestone_post_first",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "milestone_post_regular",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "milestone_problem",
                table: "settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "milestone_regular_at",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "milestone_regular_role_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "milestone_visits_read_to",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "milestones_from",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "milestones_counted_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "event_attendee",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_attendee", x => new { x.user_id, x.event_id, x.occurrence_starts_at });
                });

            migrationBuilder.CreateTable(
                name: "member_milestone",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    last_visit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    back_after_days = table.Column<int>(type: "integer", nullable: true),
                    back_handled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    regular_handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    regular_role_given_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_milestone", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_attendee_counted_at",
                table: "event_attendee",
                column: "counted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_attendee");

            migrationBuilder.DropTable(
                name: "member_milestone");

            migrationBuilder.DropColumn(
                name: "milestone_back_days",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_first_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_post_back",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_post_first",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_post_regular",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_regular_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_regular_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestone_visits_read_to",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestones_from",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "milestones_counted_at",
                table: "calendar_opening");
        }
    }
}
