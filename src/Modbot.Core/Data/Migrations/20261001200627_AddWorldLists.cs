using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "world_list_id",
                table: "calendar_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "world_picked_for",
                table: "calendar_event",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "world_list",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_world_list", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "world_pick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    people = table.Column<int>(type: "integer", nullable: true),
                    picked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    picked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    put_back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_world_pick", x => x.id);
                    table.ForeignKey(
                        name: "fk_world_pick_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "world_list_item",
                columns: table => new
                {
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    min_players = table.Column<int>(type: "integer", nullable: true),
                    max_players = table.Column<int>(type: "integer", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_world_list_item", x => new { x.list_id, x.world_id });
                    table.ForeignKey(
                        name: "fk_world_list_item_world_list_list_id",
                        column: x => x.list_id,
                        principalTable: "world_list",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "world_list_shuffle",
                columns: table => new
                {
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    world_order = table.Column<string>(type: "jsonb", nullable: false),
                    played = table.Column<string>(type: "jsonb", nullable: false),
                    last_played = table.Column<string>(type: "text", nullable: true),
                    round = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_world_list_shuffle", x => new { x.list_id, x.event_id });
                    table.ForeignKey(
                        name: "fk_world_list_shuffle_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_world_list_shuffle_world_list_list_id",
                        column: x => x.list_id,
                        principalTable: "world_list",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_event_world_list_id",
                table: "calendar_event",
                column: "world_list_id");

            migrationBuilder.CreateIndex(
                name: "ix_world_list_shuffle_event_id",
                table: "world_list_shuffle",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_world_pick_event_date",
                table: "world_pick",
                columns: new[] { "event_id", "occurrence_starts_at", "picked_at" });

            migrationBuilder.CreateIndex(
                name: "ux_world_pick_date",
                table: "world_pick",
                columns: new[] { "event_id", "occurrence_starts_at" },
                unique: true,
                filter: "kind = 'date' AND put_back_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_calendar_event_world_lists_world_list_id",
                table: "calendar_event",
                column: "world_list_id",
                principalTable: "world_list",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_calendar_event_world_lists_world_list_id",
                table: "calendar_event");

            migrationBuilder.DropTable(
                name: "world_list_item");

            migrationBuilder.DropTable(
                name: "world_list_shuffle");

            migrationBuilder.DropTable(
                name: "world_pick");

            migrationBuilder.DropTable(
                name: "world_list");

            migrationBuilder.DropIndex(
                name: "ix_calendar_event_world_list_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "world_list_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "world_picked_for",
                table: "calendar_event");
        }
    }
}
