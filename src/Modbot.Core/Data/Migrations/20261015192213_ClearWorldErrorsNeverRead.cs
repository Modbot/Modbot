using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearWorldErrorsNeverRead : Migration
    {
        /// <summary>
        /// Before the sweep wrote only real VRChat refusals, it stored an error for every failure
        /// (no account set up, a network fault, a 5xx). Seen and the save check read "error and never
        /// read" as refused, so real worlds were hidden and could not be added. Clearing those errors
        /// on worlds never read makes the sweep ask VRChat again. Worlds that were read keep theirs.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE vrchat_world SET refresh_error = NULL WHERE last_refreshed_at IS NULL AND refresh_error IS NOT NULL;");
        }

        /// <summary>
        /// Nothing to undo: the cleared errors are not kept anywhere, so they cannot be restored.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
