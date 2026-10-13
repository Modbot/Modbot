using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGallerySubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_gallery_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gallery_submission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_message_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attachment_index = table.Column<int>(type: "integer", nullable: false),
                    discord_channel_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message_url = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    author_discord_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vr_chat_file_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gallery_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    vr_chat_image_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gallery_submission", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gallery_submission_author",
                table: "gallery_submission",
                column: "author_discord_id");

            migrationBuilder.CreateIndex(
                name: "ix_gallery_submission_status",
                table: "gallery_submission",
                columns: new[] { "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_gallery_submission_message",
                table: "gallery_submission",
                columns: new[] { "discord_message_id", "attachment_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gallery_submission");

            migrationBuilder.DropColumn(
                name: "discord_gallery_channel_id",
                table: "settings");
        }
    }
}
