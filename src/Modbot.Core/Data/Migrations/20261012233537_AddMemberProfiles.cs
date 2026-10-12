using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_profile_field",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    default_visibility = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_profile_field", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "member_profile_option",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_profile_option", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_profile_option_member_profile_field_field_id",
                        column: x => x.field_id,
                        principalTable: "member_profile_field",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "member_profile_value",
                columns: table => new
                {
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    visibility = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_profile_value", x => new { x.field_id, x.discord_user_id });
                    table.ForeignKey(
                        name: "fk_member_profile_value_member_profile_field_field_id",
                        column: x => x.field_id,
                        principalTable: "member_profile_field",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_member_profile_value_member_profile_option_option_id",
                        column: x => x.option_id,
                        principalTable: "member_profile_option",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_profile_field_order",
                table: "member_profile_field",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "ix_member_profile_option_field",
                table: "member_profile_option",
                columns: new[] { "field_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_member_profile_value_option_id",
                table: "member_profile_value",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_profile_value_user",
                table: "member_profile_value",
                column: "discord_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_profile_value");

            migrationBuilder.DropTable(
                name: "member_profile_option");

            migrationBuilder.DropTable(
                name: "member_profile_field");
        }
    }
}
