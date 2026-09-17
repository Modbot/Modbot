using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoomBecameInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "share_public_rooms",
                table: "settings",
                newName: "share_public_instances");

            migrationBuilder.RenameColumn(
                name: "public_rooms_server_id",
                table: "settings",
                newName: "public_instances_server_id");

            migrationBuilder.RenameColumn(
                name: "public_rooms_secret_encrypted",
                table: "settings",
                newName: "public_instances_secret_encrypted");

            migrationBuilder.RenameColumn(
                name: "public_rooms_reported_at",
                table: "settings",
                newName: "public_instances_reported_at");

            migrationBuilder.RenameIndex(
                name: "ix_instance_head_count_room",
                table: "instance_head_count",
                newName: "ix_instance_head_count_instance");

            migrationBuilder.RenameColumn(
                name: "room_id",
                table: "calendar_opening",
                newName: "instance_id");

            // Stored words that named the same thing. The alert watchers and insight kinds are
            // saved by name, and a head count remembers whether it came from the instance's own
            // page or the group's list; "room" was the word for the page. Fact type strings and the
            // keys inside fact payloads never said "room" and are not touched.
            migrationBuilder.Sql("""
                UPDATE modbot_alert SET watcher = 'instances-opened' WHERE watcher = 'rooms-opened';
                UPDATE modbot_alert SET watcher = 'instance-filling' WHERE watcher = 'room-filling';
                UPDATE modbot_alert SET watcher = 'instance-unwatched' WHERE watcher = 'room-unwatched';
                UPDATE modbot_alert_watch SET watcher = 'instances-opened' WHERE watcher = 'rooms-opened';
                UPDATE modbot_alert_watch SET watcher = 'instance-filling' WHERE watcher = 'room-filling';
                UPDATE modbot_alert_watch SET watcher = 'instance-unwatched' WHERE watcher = 'room-unwatched';
                UPDATE modbot_insight SET kind = 'instances' WHERE kind = 'rooms';
                UPDATE modbot_insight_schedule SET kind = 'instances' WHERE kind = 'rooms';
                UPDATE vrchat_instance SET head_count_source = 'page' WHERE head_count_source = 'room';
                UPDATE instance_head_count SET source = 'page' WHERE source = 'room';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE modbot_alert SET watcher = 'rooms-opened' WHERE watcher = 'instances-opened';
                UPDATE modbot_alert SET watcher = 'room-filling' WHERE watcher = 'instance-filling';
                UPDATE modbot_alert SET watcher = 'room-unwatched' WHERE watcher = 'instance-unwatched';
                UPDATE modbot_alert_watch SET watcher = 'rooms-opened' WHERE watcher = 'instances-opened';
                UPDATE modbot_alert_watch SET watcher = 'room-filling' WHERE watcher = 'instance-filling';
                UPDATE modbot_alert_watch SET watcher = 'room-unwatched' WHERE watcher = 'instance-unwatched';
                UPDATE modbot_insight SET kind = 'rooms' WHERE kind = 'instances';
                UPDATE modbot_insight_schedule SET kind = 'rooms' WHERE kind = 'instances';
                UPDATE vrchat_instance SET head_count_source = 'room' WHERE head_count_source = 'page';
                UPDATE instance_head_count SET source = 'room' WHERE source = 'page';
                """);

            migrationBuilder.RenameColumn(
                name: "share_public_instances",
                table: "settings",
                newName: "share_public_rooms");

            migrationBuilder.RenameColumn(
                name: "public_instances_server_id",
                table: "settings",
                newName: "public_rooms_server_id");

            migrationBuilder.RenameColumn(
                name: "public_instances_secret_encrypted",
                table: "settings",
                newName: "public_rooms_secret_encrypted");

            migrationBuilder.RenameColumn(
                name: "public_instances_reported_at",
                table: "settings",
                newName: "public_rooms_reported_at");

            migrationBuilder.RenameIndex(
                name: "ix_instance_head_count_instance",
                table: "instance_head_count",
                newName: "ix_instance_head_count_room");

            migrationBuilder.RenameColumn(
                name: "instance_id",
                table: "calendar_opening",
                newName: "room_id");
        }
    }
}
