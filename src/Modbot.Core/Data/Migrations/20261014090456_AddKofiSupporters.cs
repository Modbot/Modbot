using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKofiSupporters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "kofi_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kofi_verification_token_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supporter_thanks_text",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supporter_tier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    discord_role_id = table.Column<string>(type: "text", nullable: true),
                    vr_chat_role_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supporter_tier", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supporter",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payer_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: true),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    thanks_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    given_discord_role_id = table.Column<string>(type: "text", nullable: true),
                    given_vr_chat_role_id = table.Column<string>(type: "text", nullable: true),
                    given_vr_chat_user_id = table.Column<string>(type: "text", nullable: true),
                    problem = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    try_again_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supporter", x => x.id);
                    table.ForeignKey(
                        name: "fk_supporter_supporter_tier_tier_id",
                        column: x => x.tier_id,
                        principalTable: "supporter_tier",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "supporter_payment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    supporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supporter_payment", x => x.id);
                    table.ForeignKey(
                        name: "fk_supporter_payment_supporter_supporter_id",
                        column: x => x.supporter_id,
                        principalTable: "supporter",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_supporter_discord_user",
                table: "supporter",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_supporter_tier_id",
                table: "supporter",
                column: "tier_id");

            migrationBuilder.CreateIndex(
                name: "ux_supporter_payer",
                table: "supporter",
                columns: new[] { "provider", "payer_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supporter_payment_supporter",
                table: "supporter_payment",
                column: "supporter_id");

            migrationBuilder.CreateIndex(
                name: "ux_supporter_payment_code",
                table: "supporter_payment",
                columns: new[] { "provider", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_supporter_tier_key",
                table: "supporter_tier",
                columns: new[] { "provider", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supporter_payment");

            migrationBuilder.DropTable(
                name: "supporter");

            migrationBuilder.DropTable(
                name: "supporter_tier");

            migrationBuilder.DropColumn(
                name: "kofi_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "kofi_verification_token_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "supporter_thanks_text",
                table: "settings");
        }
    }
}
