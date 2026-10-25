using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AskForAClipWithEveryAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "clip_on_bans",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "clip_on_kicks",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "clip_on_warnings",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "clip_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fact_id = table.Column<long>(type: "bigint", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    moderator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: true),
                    instance_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    blob_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clip_request", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clip_request_cursor",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    read_through = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clip_request_cursor", x => x.id);
                    table.CheckConstraint("ck_clip_request_cursor_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_clip_request_moderator",
                table: "clip_request",
                columns: new[] { "moderator_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clip_request_waiting",
                table: "clip_request",
                column: "device_id",
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ux_clip_request_fact",
                table: "clip_request",
                column: "fact_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clip_request");

            migrationBuilder.DropTable(
                name: "clip_request_cursor");

            migrationBuilder.DropColumn(
                name: "clip_on_bans",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "clip_on_kicks",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "clip_on_warnings",
                table: "settings");
        }
    }
}
