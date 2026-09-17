using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
{
    /// <summary>
    /// The desktop program became the Modbot Companion, and the table of the events it backs up
    /// follows. The primary key is renamed in place rather than dropped and rebuilt: on a table
    /// of every presence event every companion has ever sent, rebuilding the key is the expensive part.
    /// </summary>
    public partial class RenameCompanionEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
migrationBuilder.RenameTable(
                name: "client_event",
                newName: "companion_event");

            migrationBuilder.RenameColumn(
                name: "client_version",
                table: "companion_event",
                newName: "companion_version");

            migrationBuilder.RenameColumn(
                name: "client_event_id",
                table: "companion_event",
                newName: "companion_event_id");

            migrationBuilder.RenameIndex(
                name: "ix_client_event_type_occurred_at",
                table: "companion_event",
                newName: "ix_companion_event_type_occurred_at");

            migrationBuilder.RenameIndex(
                name: "ix_client_event_received_at",
                table: "companion_event",
                newName: "ix_companion_event_received_at");

            migrationBuilder.RenameIndex(
                name: "ix_client_event_install_id_received_at",
                table: "companion_event",
                newName: "ix_companion_event_install_id_received_at");

            migrationBuilder.Sql("ALTER TABLE companion_event RENAME CONSTRAINT pk_client_event TO pk_companion_event;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
migrationBuilder.RenameTable(
                name: "companion_event",
                newName: "client_event");

            migrationBuilder.RenameColumn(
                name: "companion_version",
                table: "client_event",
                newName: "client_version");

            migrationBuilder.RenameColumn(
                name: "companion_event_id",
                table: "client_event",
                newName: "client_event_id");

            migrationBuilder.RenameIndex(
                name: "ix_companion_event_type_occurred_at",
                table: "client_event",
                newName: "ix_client_event_type_occurred_at");

            migrationBuilder.RenameIndex(
                name: "ix_companion_event_received_at",
                table: "client_event",
                newName: "ix_client_event_received_at");

            migrationBuilder.RenameIndex(
                name: "ix_companion_event_install_id_received_at",
                table: "client_event",
                newName: "ix_client_event_install_id_received_at");

            migrationBuilder.Sql("ALTER TABLE client_event RENAME CONSTRAINT pk_companion_event TO pk_client_event;");
        }
    }
}
