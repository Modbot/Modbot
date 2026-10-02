using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepACroppedDiscordPicture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "cover_picture_id",
                table: "calendar_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_cover_picture",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_cover_picture", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_event_cover_picture_id",
                table: "calendar_event",
                column: "cover_picture_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_cover_picture_created_at",
                table: "calendar_cover_picture",
                column: "created_at");

            migrationBuilder.AddForeignKey(
                name: "fk_calendar_event_calendar_cover_picture_cover_picture_id",
                table: "calendar_event",
                column: "cover_picture_id",
                principalTable: "calendar_cover_picture",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_calendar_event_calendar_cover_picture_cover_picture_id",
                table: "calendar_event");

            migrationBuilder.DropTable(
                name: "calendar_cover_picture");

            migrationBuilder.DropIndex(
                name: "ix_calendar_event_cover_picture_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "cover_picture_id",
                table: "calendar_event");
        }
    }
}
