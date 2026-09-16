using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Sends the log to Modbot Cloud as the server the registry already knows, rather than as an
    /// install of its own.
    /// </summary>
    /// <remarks>
    /// The two columns removed here were added the same day and never shipped. A deployment that
    /// registered with Cloud twice would give Cloud two ids for one thing and no way to join them --
    /// and that join is exactly what lets the account which claimed the server read its own logs.
    /// The credential is now the one <c>ServerReporter</c> establishes, in
    /// <c>cloud_server_id</c> and <c>cloud_server_secret_encrypted</c>.
    /// </remarks>
    public partial class ShipLogsAsTheRegisteredServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cloud_log_install_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_secret_encrypted",
                table: "settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cloud_log_install_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_log_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);
        }
    }
}
