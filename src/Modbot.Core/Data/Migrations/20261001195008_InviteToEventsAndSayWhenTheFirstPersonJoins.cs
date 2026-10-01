using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InviteToEventsAndSayWhenTheFirstPersonJoins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "first_join_discord_post_error",
                table: "calendar_opening",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_join_discord_posted_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_join_vrchat_post_error",
                table: "calendar_opening",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_join_vrchat_posted_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invites_queued_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "announce_first_join_in_discord",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "announce_first_join_in_vrchat",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "invite_host_user_id",
                table: "calendar_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "invite_list_id",
                table: "calendar_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invite_staff_user_ids",
                table: "calendar_event",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "calendar_invite",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    person_key = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: true),
                    discord_user_id = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    problem = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tried_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    messaged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_invite", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_invite_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vrchat_friend",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    is_friend = table.Column<bool>(type: "boolean", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    learned_from = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vrchat_friend", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_invite_discord_user_id",
                table: "calendar_invite",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_invite_state",
                table: "calendar_invite",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_invite_vrchat_user_id",
                table: "calendar_invite",
                column: "vrchat_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_calendar_invite_person",
                table: "calendar_invite",
                columns: new[] { "event_id", "occurrence_starts_at", "person_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_invite");

            migrationBuilder.DropTable(
                name: "vrchat_friend");

            migrationBuilder.DropColumn(
                name: "first_join_discord_post_error",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "first_join_discord_posted_at",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "first_join_vrchat_post_error",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "first_join_vrchat_posted_at",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "invites_queued_at",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "announce_first_join_in_discord",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "announce_first_join_in_vrchat",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "invite_host_user_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "invite_list_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "invite_staff_user_ids",
                table: "calendar_event");
        }
    }
}
