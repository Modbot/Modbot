using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Posts for the Marketing tab (posts design §2.2): <c>post</c> and one <c>post_destination</c>
    /// per site it goes to, and the two switches of the Settings topic "Posts". Pause all posting
    /// starts off; Discord posts start on, on old installs too, because nothing goes out unless a
    /// person ticks Discord on a post.
    /// </summary>
    public partial class SendPostsFromTheMarketingTab : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discord_posts_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "posts_paused",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "post",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    picture_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    send_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    date_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post", x => x.id);
                    table.ForeignKey(
                        name: "fk_post_calendar_cover_picture_picture_id",
                        column: x => x.picture_id,
                        principalTable: "calendar_cover_picture",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "post_destination",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    title_override = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    text_override = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_key = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sent_title = table.Column<string>(type: "text", nullable: true),
                    sent_text = table.Column<string>(type: "text", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    missing_permission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reply_to_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remove_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    may_be_sent = table.Column<bool>(type: "boolean", nullable: false),
                    send_if_missing = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_destination", x => x.id);
                    table.ForeignKey(
                        name: "fk_post_destination_post_post_id",
                        column: x => x.post_id,
                        principalTable: "post",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_post_picture_id",
                table: "post",
                column: "picture_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_status_send_at",
                table: "post",
                columns: new[] { "status", "send_at" });

            migrationBuilder.CreateIndex(
                name: "ux_post_event_kind",
                table: "post",
                columns: new[] { "event_id", "kind" },
                unique: true,
                filter: "kind IS NOT NULL AND date_starts_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_post_event_kind_date",
                table: "post",
                columns: new[] { "event_id", "kind", "date_starts_at" },
                unique: true,
                filter: "kind IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_destination_network_state",
                table: "post_destination",
                columns: new[] { "network", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_post_destination_post_network",
                table: "post_destination",
                columns: new[] { "post_id", "network" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "post_destination");

            migrationBuilder.DropTable(
                name: "post");

            migrationBuilder.DropColumn(
                name: "discord_posts_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "posts_paused",
                table: "settings");
        }
    }
}
