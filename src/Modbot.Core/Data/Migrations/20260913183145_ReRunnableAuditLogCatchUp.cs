using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// One column: which version of the one-off audit-log walk the stored cursor belongs to.
    /// </summary>
    /// <remarks>
    /// Defaults to zero, which is deliberately below the first <c>GroupAuditLogSync.CatchUpVersion</c>,
    /// so every deployment from before this migration walks its history again on the next pass.
    /// That is the point: the first producer deployment counted and dropped about 720 entries it
    /// had no name for, and VRChat keeps roughly thirty days of audit log. Re-reading the entries
    /// already stored is a no-op -- they are recognised by id -- so the walk costs only requests.
    /// </remarks>
    public partial class ReRunnableAuditLogCatchUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "audit_log_catch_up_version",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audit_log_catch_up_version",
                table: "settings");
        }
    }
}
