using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "problem_avatar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_problem_avatar", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_problem_avatar_name",
                table: "problem_avatar",
                column: "name_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problem_avatar");
        }
    }
}
