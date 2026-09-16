using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
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
                table: "instance_log",
                newName: "server_id");

            migrationBuilder.RenameIndex(
                name: "ix_instance_log_install_received_at",
                table: "instance_log",
                newName: "ix_instance_log_server_received_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "server_id",
                table: "instance_log",
                newName: "install_id");

            migrationBuilder.RenameIndex(
                name: "ix_instance_log_server_received_at",
                table: "instance_log",
                newName: "ix_instance_log_install_received_at");
        }
    }
}
