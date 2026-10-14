using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vote",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    show_counts = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    anonymous = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vote_option",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote_option", x => x.id);
                    table.ForeignKey(
                        name: "fk_vote_option_vote_vote_id",
                        column: x => x.vote_id,
                        principalTable: "vote",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vote_pick",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    voted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote_pick", x => x.id);
                    table.ForeignKey(
                        name: "fk_vote_pick_vote_option_option_id",
                        column: x => x.option_id,
                        principalTable: "vote_option",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_vote_pick_vote_vote_id",
                        column: x => x.vote_id,
                        principalTable: "vote",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vote_created",
                table: "vote",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_vote_option_order",
                table: "vote_option",
                columns: new[] { "vote_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_vote_pick_option",
                table: "vote_pick",
                columns: new[] { "vote_id", "option_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vote_pick_option_id",
                table: "vote_pick",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "ux_vote_pick_person",
                table: "vote_pick",
                columns: new[] { "vote_id", "discord_user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vote_pick");

            migrationBuilder.DropTable(
                name: "vote_option");

            migrationBuilder.DropTable(
                name: "vote");
        }
    }
}
