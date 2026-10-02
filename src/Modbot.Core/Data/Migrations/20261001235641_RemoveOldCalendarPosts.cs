using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Lets Modbot take its own calendar posts off Discord a day after their event or date ended
    /// (calendar design §3.3): a list of cards that had their last word, which a repeating event's
    /// post would otherwise forget when it moves on to the next date, and when a one-date
    /// "Cancelled" line came down. A whole-event "Cancelled" line is marked on its own place row,
    /// which needs no new column.
    /// </summary>
    public partial class RemoveOldCalendarPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancel_post_removed_at",
                table: "calendar_date_change",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_old_post",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_old_post", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_old_post_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_old_post_event_id",
                table: "calendar_old_post",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_old_post_up",
                table: "calendar_old_post",
                column: "ends_at",
                filter: "removed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_old_post");

            migrationBuilder.DropColumn(
                name: "cancel_post_removed_at",
                table: "calendar_date_change");
        }
    }
}
