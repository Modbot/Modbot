using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetModeratorsWatchAPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "person_watch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_platform = table.Column<short>(type: "smallint", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    follow_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    follow_up_reminded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ended_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_watch", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_person_watch_person",
                table: "person_watch",
                columns: new[] { "subject_platform", "subject_id", "set_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_person_watch_standing",
                table: "person_watch",
                columns: new[] { "subject_platform", "subject_id" },
                unique: true,
                filter: "ended_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_watch");
        }
    }
}
