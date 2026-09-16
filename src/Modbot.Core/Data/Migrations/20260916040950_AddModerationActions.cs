using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "moderation_action",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    moderator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    moderator_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason_ids = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    rate_limited = table.Column<bool>(type: "boolean", nullable: false),
                    case_file_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_action", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_action_user",
                table: "moderation_action",
                columns: new[] { "user_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_moderation_action_key",
                table: "moderation_action",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moderation_action");
        }
    }
}
