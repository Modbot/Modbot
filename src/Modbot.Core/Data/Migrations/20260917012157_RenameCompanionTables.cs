using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The desktop program became the Modbot Companion, and its two tables follow. Written by hand:
    /// EF matched neither renamed entity to its old table and scaffolded a drop and a create, which
    /// would have thrown away every paired device.
    /// </summary>
    public partial class RenameCompanionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "client_device", newName: "companion_device");
            migrationBuilder.RenameColumn(name: "client_version", table: "companion_device", newName: "companion_version");
            migrationBuilder.Sql("ALTER TABLE companion_device RENAME CONSTRAINT pk_client_device TO pk_companion_device;");
            migrationBuilder.RenameIndex(name: "ix_client_device_token_hash", table: "companion_device", newName: "ix_companion_device_token_hash");

            migrationBuilder.RenameTable(name: "client_pairing_code", newName: "companion_pairing_code");
            migrationBuilder.Sql("ALTER TABLE companion_pairing_code RENAME CONSTRAINT pk_client_pairing_code TO pk_companion_pairing_code;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE companion_pairing_code RENAME CONSTRAINT pk_companion_pairing_code TO pk_client_pairing_code;");
            migrationBuilder.RenameTable(name: "companion_pairing_code", newName: "client_pairing_code");

            migrationBuilder.RenameIndex(name: "ix_companion_device_token_hash", table: "companion_device", newName: "ix_client_device_token_hash");
            migrationBuilder.Sql("ALTER TABLE companion_device RENAME CONSTRAINT pk_companion_device TO pk_client_device;");
            migrationBuilder.RenameColumn(name: "companion_version", table: "companion_device", newName: "client_version");
            migrationBuilder.RenameTable(name: "companion_device", newName: "client_device");
        }
    }
}
