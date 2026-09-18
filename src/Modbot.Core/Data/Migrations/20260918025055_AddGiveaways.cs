using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGiveaways : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "giveaway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prize = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    opens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    draw_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    winner_count = table.Column<int>(type: "integer", nullable: false),
                    entry_way = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    emoji = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    exclusions = table.Column<string>(type: "jsonb", nullable: false),
                    weighting = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    weight_cap = table.Column<long>(type: "bigint", nullable: true),
                    post_to_channel = table.Column<bool>(type: "boolean", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    seed_promise = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seed_encrypted = table.Column<string>(type: "text", nullable: false),
                    draw_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_giveaway", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "giveaway_draw",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    giveaway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    drawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    drawn_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    seed = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    seed_promise = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    winner_count = table.Column<int>(type: "integer", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    exclusions = table.Column<string>(type: "jsonb", nullable: false),
                    weighting = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    weight_cap = table.Column<long>(type: "bigint", nullable: true),
                    entrant_count = table.Column<int>(type: "integer", nullable: false),
                    in_draw_count = table.Column<int>(type: "integer", nullable: false),
                    total_weight = table.Column<long>(type: "bigint", nullable: false),
                    from_polled_data = table.Column<bool>(type: "boolean", nullable: false),
                    close_calls = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_giveaway_draw", x => x.id);
                    table.ForeignKey(
                        name: "fk_giveaway_draw_giveaway_giveaway_id",
                        column: x => x.giveaway_id,
                        principalTable: "giveaway",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "giveaway_entry",
                columns: table => new
                {
                    giveaway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    qualified_on_entry = table.Column<bool>(type: "boolean", nullable: false),
                    kept_out = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_giveaway_entry", x => new { x.giveaway_id, x.discord_user_id });
                    table.ForeignKey(
                        name: "fk_giveaway_entry_giveaway_giveaway_id",
                        column: x => x.giveaway_id,
                        principalTable: "giveaway",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "giveaway_post",
                columns: table => new
                {
                    giveaway_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    sent_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failed_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    announced_draws = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_giveaway_post", x => x.giveaway_id);
                    table.ForeignKey(
                        name: "fk_giveaway_post_giveaway_giveaway_id",
                        column: x => x.giveaway_id,
                        principalTable: "giveaway",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "giveaway_entrant",
                columns: table => new
                {
                    draw_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    vrchat_user_id = table.Column<string>(type: "text", nullable: true),
                    discord_user_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    weight = table.Column<long>(type: "bigint", nullable: false),
                    measured = table.Column<decimal>(type: "numeric", nullable: false),
                    kept_out = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    because = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    from_polled_data = table.Column<bool>(type: "boolean", nullable: false),
                    close_call = table.Column<bool>(type: "boolean", nullable: false),
                    winner_rank = table.Column<int>(type: "integer", nullable: true),
                    purged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_giveaway_entrant", x => new { x.draw_id, x.position });
                    table.ForeignKey(
                        name: "fk_giveaway_entrant_giveaway_draw_draw_id",
                        column: x => x.draw_id,
                        principalTable: "giveaway_draw",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_giveaway_state",
                table: "giveaway",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ux_giveaway_draw_number",
                table: "giveaway_draw",
                columns: new[] { "giveaway_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_giveaway_entrant_key",
                table: "giveaway_entrant",
                column: "key");

            migrationBuilder.CreateIndex(
                name: "ix_giveaway_entry_person",
                table: "giveaway_entry",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_giveaway_post_message",
                table: "giveaway_post",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "giveaway_entrant");

            migrationBuilder.DropTable(
                name: "giveaway_entry");

            migrationBuilder.DropTable(
                name: "giveaway_post");

            migrationBuilder.DropTable(
                name: "giveaway_draw");

            migrationBuilder.DropTable(
                name: "giveaway");
        }
    }
}
