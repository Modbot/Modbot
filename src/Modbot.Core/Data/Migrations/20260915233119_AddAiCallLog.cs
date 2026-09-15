using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A month, matching the entity's default: an existing deployment must not read 0 and
            // decide to keep every call for ever.
            migrationBuilder.AddColumn<int>(
                name: "ai_call_log_keep_days",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "ai_fallback_model",
                table: "settings",
                type: "text",
                nullable: true);

            // Five, matching the entity's default. 0 would be clamped back to one profile per
            // call, so an existing deployment would silently get none of the batching.
            migrationBuilder.AddColumn<int>(
                name: "ai_moderation_profile_batch_size",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<Guid>(
                name: "call_id",
                table: "ai_flag",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_call",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_asked = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model_answered = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fallback = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    reported_cost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt = table.Column<string>(type: "text", nullable: true),
                    answer = table.Column<string>(type: "text", nullable: true),
                    flagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_call", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_call_at",
                table: "ai_call",
                columns: new[] { "at", "feature", "outcome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_call");

            migrationBuilder.DropColumn(
                name: "ai_call_log_keep_days",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_fallback_model",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_moderation_profile_batch_size",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "call_id",
                table: "ai_flag");
        }
    }
}
