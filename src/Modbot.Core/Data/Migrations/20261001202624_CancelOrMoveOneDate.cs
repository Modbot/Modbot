using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CancelOrMoveOneDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_date_change",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancel_post_channel_id = table.Column<string>(type: "text", nullable: true),
                    cancel_post_id = table.Column<string>(type: "text", nullable: true),
                    vrchat_id = table.Column<string>(type: "text", nullable: true),
                    vrchat_sent_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    vrchat_failed_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    vrchat_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    vrchat_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_date_change", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_date_change_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_calendar_date_change_date",
                table: "calendar_date_change",
                columns: new[] { "event_id", "planned_starts_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_date_change");
        }
    }
}
