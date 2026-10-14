using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgeCheckSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "age_check_delete_ticket",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "age_check_give_role_ids",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "age_check_group_role_id",
                table: "settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "age_check_remove_role_ids",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "age_check_ticket_category_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "age_check_delete_ticket",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "age_check_give_role_ids",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "age_check_group_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "age_check_remove_role_ids",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "age_check_ticket_category_id",
                table: "settings");
        }
    }
}
