using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// A Modbot deployment is a "server", and "instance" is left to VRChat's own meaning. Written by
    /// hand as renames: the scaffolder, seeing three entities go and three arrive, proposed dropping
    /// the tables and making new ones, which would have lost every noted address, every visitor's
    /// list and every alert anyone had set up. The rows, the indexes and the primary keys are the
    /// same; only the words changed.
    /// </summary>
    public partial class ModbotInstanceBecameServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Addresses noted from my.modbot.co page visits.
            migrationBuilder.RenameTable(
                name: "page_instance",
                newName: "page_server");

            migrationBuilder.RenameColumn(
                name: "instance_url",
                table: "page_server",
                newName: "server_url");

            migrationBuilder.RenameIndex(
                name: "ix_page_instance_last_seen_at",
                table: "page_server",
                newName: "ix_page_server_last_seen_at");

            migrationBuilder.Sql("ALTER TABLE page_server RENAME CONSTRAINT pk_page_instance TO pk_page_server;");

            // The same addresses against the visitor's IP address.
            migrationBuilder.RenameTable(
                name: "visitor_instance",
                newName: "visitor_server");

            migrationBuilder.RenameColumn(
                name: "instance_url",
                table: "visitor_server",
                newName: "server_url");

            migrationBuilder.RenameIndex(
                name: "ix_visitor_instance_instance_url",
                table: "visitor_server",
                newName: "ix_visitor_server_server_url");

            migrationBuilder.Sql("ALTER TABLE visitor_server RENAME CONSTRAINT pk_visitor_instance TO pk_visitor_server;");

            // What a register visit learned. The table already had the right name; its key did not.
            migrationBuilder.RenameColumn(
                name: "instance_url",
                table: "visited_server",
                newName: "server_url");

            // Cloud's watch on a server that has gone quiet.
            migrationBuilder.RenameTable(
                name: "instance_alert",
                newName: "server_alert");

            migrationBuilder.RenameIndex(
                name: "ix_instance_alert_watched",
                table: "server_alert",
                newName: "ix_server_alert_watched");

            migrationBuilder.Sql("ALTER TABLE server_alert RENAME CONSTRAINT pk_instance_alert TO pk_server_alert;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE server_alert RENAME CONSTRAINT pk_server_alert TO pk_instance_alert;");

            migrationBuilder.RenameIndex(
                name: "ix_server_alert_watched",
                table: "server_alert",
                newName: "ix_instance_alert_watched");

            migrationBuilder.RenameTable(
                name: "server_alert",
                newName: "instance_alert");

            migrationBuilder.RenameColumn(
                name: "server_url",
                table: "visited_server",
                newName: "instance_url");

            migrationBuilder.Sql("ALTER TABLE visitor_server RENAME CONSTRAINT pk_visitor_server TO pk_visitor_instance;");

            migrationBuilder.RenameIndex(
                name: "ix_visitor_server_server_url",
                table: "visitor_server",
                newName: "ix_visitor_instance_instance_url");

            migrationBuilder.RenameColumn(
                name: "server_url",
                table: "visitor_server",
                newName: "instance_url");

            migrationBuilder.RenameTable(
                name: "visitor_server",
                newName: "visitor_instance");

            migrationBuilder.Sql("ALTER TABLE page_server RENAME CONSTRAINT pk_page_server TO pk_page_instance;");

            migrationBuilder.RenameIndex(
                name: "ix_page_server_last_seen_at",
                table: "page_server",
                newName: "ix_page_instance_last_seen_at");

            migrationBuilder.RenameColumn(
                name: "server_url",
                table: "page_server",
                newName: "instance_url");

            migrationBuilder.RenameTable(
                name: "page_server",
                newName: "page_instance");
        }
    }
}
