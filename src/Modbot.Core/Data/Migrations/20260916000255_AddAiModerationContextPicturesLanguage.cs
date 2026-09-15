using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModerationContextPicturesLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "modbot_review",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "check_pictures",
                table: "ai_topic",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Five, not zero: a rule that existed before context did should behave like one made
            // today, and five is what a new rule starts on (ContextMessageCounts.Default).
            migrationBuilder.AddColumn<int>(
                name: "context_messages",
                table: "ai_topic",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "open_review_for_each_flag",
                table: "ai_topic",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "check_pictures",
                table: "ai_term_list",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "context_messages",
                table: "ai_term_list",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "open_review_for_each_flag",
                table: "ai_term_list",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at",
                table: "ai_flag",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "confirmed_by_user_id",
                table: "ai_flag",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmed_by_username",
                table: "ai_flag",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // An empty array, not an empty string: the column is jsonb and "" is not JSON.
            migrationBuilder.AddColumn<string>(
                name: "context_message_ids",
                table: "ai_flag",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "ai_flag",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "picture",
                table: "ai_flag",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "picture_url",
                table: "ai_flag",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "review_id",
                table: "ai_flag",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_flag_language",
                table: "ai_flag",
                columns: new[] { "rule_id", "language" },
                filter: "language IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_flag_language",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "modbot_review");

            migrationBuilder.DropColumn(
                name: "check_pictures",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "context_messages",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "open_review_for_each_flag",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "check_pictures",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "context_messages",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "open_review_for_each_flag",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "confirmed_at",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "confirmed_by_user_id",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "confirmed_by_username",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "context_message_ids",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "language",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "picture",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "picture_url",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "review_id",
                table: "ai_flag");
        }
    }
}
