using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportCanKeepFactsAlreadyKnown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "dedup",
                table: "import",
                type: "boolean",
                nullable: false,
                // True, not EF's false for a new bool: every import that has already run skipped
                // the records Modbot already had, and a row reading "off" would say it did the
                // opposite of what it did.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dedup",
                table: "import");
        }
    }
}
