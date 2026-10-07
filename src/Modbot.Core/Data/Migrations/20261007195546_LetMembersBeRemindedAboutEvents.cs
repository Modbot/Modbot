using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetMembersBeRemindedAboutEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_reminder",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    minutes_before = table.Column<int>(type: "integer", nullable: false),
                    remind_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_reminder", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_reminder_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_reminder_discord_user_id",
                table: "event_reminder",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_reminder_due",
                table: "event_reminder",
                column: "remind_at",
                filter: "state = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "ix_event_reminder_event_id",
                table: "event_reminder",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_reminder_updated_at",
                table: "event_reminder",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ux_event_reminder_waiting",
                table: "event_reminder",
                columns: new[] { "discord_user_id", "event_id", "occurrence_starts_at" },
                unique: true,
                filter: "state = 'waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_reminder");
        }
    }
}
