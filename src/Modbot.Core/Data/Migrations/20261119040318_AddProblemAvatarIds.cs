using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemAvatarIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_problem_avatar_name",
                table: "problem_avatar");

            migrationBuilder.AddColumn<string>(
                name: "avatar_id",
                table: "problem_avatar",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_problem_avatar_avatar_id",
                table: "problem_avatar",
                column: "avatar_id",
                unique: true,
                filter: "avatar_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_problem_avatar_name",
                table: "problem_avatar",
                column: "name_key",
                unique: true,
                filter: "name_key <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_problem_avatar_avatar_id",
                table: "problem_avatar");

            migrationBuilder.DropIndex(
                name: "ux_problem_avatar_name",
                table: "problem_avatar");

            migrationBuilder.DropColumn(
                name: "avatar_id",
                table: "problem_avatar");

            migrationBuilder.CreateIndex(
                name: "ux_problem_avatar_name",
                table: "problem_avatar",
                column: "name_key",
                unique: true);
        }
    }
}
