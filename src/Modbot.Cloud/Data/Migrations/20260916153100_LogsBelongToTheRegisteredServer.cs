using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// The log lines a deployment sends belong to the server the registry knows, not to an install
    /// of its own.
    /// </summary>
    /// <remarks>
    /// A deployment registering with Cloud twice would give Cloud two ids for one thing and no way
    /// to join them -- and that join is exactly what lets the account which claimed the server read
    /// its own logs. Renamed the same day it was added, before anything shipped.
    /// </remarks>
    public partial class LogsBelongToTheRegisteredServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "install_id",
                table: "instance_alert",
                newName: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "server_id",
                table: "instance_alert",
                newName: "install_id");
        }
    }
}
