using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlagRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "flag_rules",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automod_flag_person",
                table: "automod_flag",
                columns: new[] { "subject_platform", "subject_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automod_flag_person",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "flag_rules",
                table: "settings");
        }
    }
}
