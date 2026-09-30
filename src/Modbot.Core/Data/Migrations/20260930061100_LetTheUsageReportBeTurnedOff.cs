using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetTheUsageReportBeTurnedOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "send_usage_report",
                table: "settings",
                type: "boolean",
                nullable: false,
                // On, like the C# default. The column is added to a row that already exists, and
                // a Modbot that upgraded must keep sending the report it already sends until its
                // owner turns it off. MODBOT_CLOUD_DISABLED stops it whatever this says.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "send_usage_report",
                table: "settings");
        }
    }
}
