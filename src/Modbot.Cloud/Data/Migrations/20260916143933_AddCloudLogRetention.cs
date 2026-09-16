using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// How long Cloud keeps the log lines Modbot deployments send it.
    /// </summary>
    /// <remarks>
    /// Its own window rather than the events': logs are far more numerous and are only useful while
    /// somebody still remembers the problem, so 180 days rather than the events' 365.
    /// </remarks>
    public partial class AddCloudLogRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "log_keep_days",
                table: "settings",
                type: "integer",
                nullable: false,
                // 180, not EF's 0: zero means "keep forever" here, and a Cloud that upgrades into
                // this feature should get the window that was chosen for it rather than a table
                // nothing ever prunes.
                defaultValue: 180);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "log_keep_days",
                table: "settings");
        }
    }
}
