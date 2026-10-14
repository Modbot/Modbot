using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackWhichInviteMembersUsed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_invite",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    is_vanity = table.Column<bool>(type: "boolean", nullable: false),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    inviter_id = table.Column<string>(type: "text", nullable: true),
                    made_by_modbot = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_invite", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_invite_join",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    invite_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_invite_join", x => x.id);
                    table.ForeignKey(
                        name: "fk_discord_invite_join_discord_invite_invite_id",
                        column: x => x.invite_id,
                        principalTable: "discord_invite",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_invite_code",
                table: "discord_invite",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discord_invite_join_at",
                table: "discord_invite_join",
                column: "joined_at");

            migrationBuilder.CreateIndex(
                name: "ix_discord_invite_join_invite",
                table: "discord_invite_join",
                column: "invite_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_invite_join");

            migrationBuilder.DropTable(
                name: "discord_invite");
        }
    }
}
