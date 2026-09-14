using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldsAndInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vrchat_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    vr_chat_instance_id = table.Column<string>(type: "text", nullable: true),
                    group_id = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    group_access_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_user_count = table.Column<int>(type: "integer", nullable: true),
                    peak_user_count = table.Column<int>(type: "integer", nullable: true),
                    seen_in_group_list = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vrchat_instance", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vrchat_world",
                columns: table => new
                {
                    world_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    author_id = table.Column<string>(type: "text", nullable: true),
                    author_name = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    thumbnail_image_url = table.Column<string>(type: "text", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    recommended_capacity = table.Column<int>(type: "integer", nullable: true),
                    tags = table.Column<string>(type: "jsonb", nullable: true),
                    release_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vrchat_world", x => x.world_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_instance_group",
                table: "vrchat_instance",
                columns: new[] { "group_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_instance_open",
                table: "vrchat_instance",
                columns: new[] { "location", "last_seen_at" },
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_instance_world",
                table: "vrchat_instance",
                columns: new[] { "world_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_vrchat_world_unnamed",
                table: "vrchat_world",
                column: "first_seen_at",
                filter: "last_refreshed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vrchat_instance");

            migrationBuilder.DropTable(
                name: "vrchat_world");
        }
    }
}
