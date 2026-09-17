using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// The two public-feed tables under their new names. Written by hand as renames: the
    /// scaffolder, seeing two entities go and two arrive, proposed dropping the tables and making
    /// new ones, which would have emptied every group off modbot.co until its Modbot reported
    /// again. The rows, the indexes and the primary keys are the same; only the words changed.
    /// </summary>
    public partial class RoomBecameInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "rooms_server",
                newName: "instances_server");

            migrationBuilder.RenameTable(
                name: "public_room",
                newName: "public_instance");

            migrationBuilder.RenameIndex(
                name: "ix_rooms_server_group_id",
                table: "instances_server",
                newName: "ix_instances_server_group_id");

            migrationBuilder.RenameIndex(
                name: "ix_rooms_server_last_reported_at",
                table: "instances_server",
                newName: "ix_instances_server_last_reported_at");

            migrationBuilder.RenameIndex(
                name: "ix_public_room_server_id_location",
                table: "public_instance",
                newName: "ix_public_instance_server_id_location");

            migrationBuilder.Sql("ALTER TABLE instances_server RENAME CONSTRAINT pk_rooms_server TO pk_instances_server;");
            migrationBuilder.Sql("ALTER TABLE public_instance RENAME CONSTRAINT pk_public_room TO pk_public_instance;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE instances_server RENAME CONSTRAINT pk_instances_server TO pk_rooms_server;");
            migrationBuilder.Sql("ALTER TABLE public_instance RENAME CONSTRAINT pk_public_instance TO pk_public_room;");

            migrationBuilder.RenameIndex(
                name: "ix_instances_server_group_id",
                table: "instances_server",
                newName: "ix_rooms_server_group_id");

            migrationBuilder.RenameIndex(
                name: "ix_instances_server_last_reported_at",
                table: "instances_server",
                newName: "ix_rooms_server_last_reported_at");

            migrationBuilder.RenameIndex(
                name: "ix_public_instance_server_id_location",
                table: "public_instance",
                newName: "ix_public_room_server_id_location");

            migrationBuilder.RenameTable(
                name: "instances_server",
                newName: "rooms_server");

            migrationBuilder.RenameTable(
                name: "public_instance",
                newName: "public_room");
        }
    }
}
