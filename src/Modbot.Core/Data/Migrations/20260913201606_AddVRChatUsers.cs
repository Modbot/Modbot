using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The <c>vrchat_user</c> table (user profile sync design §2) and the profile sync's two
    /// cursor columns on the settings row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-checked against the design: ids are <c>text</c> with no length (foundation §3.1.1);
    /// the user-authored fields are unbounded text because VRChat's own caps on them have moved;
    /// <c>tags</c> and <c>raw_profile</c> are <c>jsonb</c> so a later question can be answered
    /// with a query instead of another fetch; <c>is_18_plus_verified</c> defaults to false and
    /// nothing in this migration or any sync ever sets it back once true.
    /// </para>
    /// <para>
    /// Two plain indexes, on <c>last_refreshed_at</c> and <c>last_seen_at</c>: the refresh queue
    /// is ordered on exactly those two columns and is asked once a second.
    /// </para>
    /// </remarks>
    public partial class AddVRChatUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "user_profile_events_read_through",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "user_profile_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "vrchat_user",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    bio = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status_description = table.Column<string>(type: "text", nullable: true),
                    pronouns = table.Column<string>(type: "text", nullable: true),
                    current_avatar_image_url = table.Column<string>(type: "text", nullable: true),
                    current_avatar_thumbnail_image_url = table.Column<string>(type: "text", nullable: true),
                    profile_picture_url = table.Column<string>(type: "text", nullable: true),
                    date_joined = table.Column<DateOnly>(type: "date", nullable: true),
                    tags = table.Column<string>(type: "jsonb", nullable: true),
                    last_platform = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    age_verification_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    age_verified = table.Column<bool>(type: "boolean", nullable: true),
                    is_18_plus_verified = table.Column<bool>(type: "boolean", nullable: false),
                    is_18_plus_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_18_plus_verified_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    is_18_plus_verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    refresh_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    not_found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_profile = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vrchat_user", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_user_last_refreshed",
                table: "vrchat_user",
                column: "last_refreshed_at");

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_user_last_seen",
                table: "vrchat_user",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "user_profile_events_read_through",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "user_profile_polled_at",
                table: "settings");
        }
    }
}
