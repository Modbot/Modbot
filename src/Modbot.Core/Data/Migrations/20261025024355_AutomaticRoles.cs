using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automatic_role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    vrchat_role_id = table.Column<string>(type: "text", nullable: true),
                    discord_role_id = table.Column<string>(type: "text", nullable: true),
                    whole_team = table.Column<bool>(type: "boolean", nullable: false),
                    who_discord_role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    who_vrchat_role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    companion_running = table.Column<bool>(type: "boolean", nullable: false),
                    clips_ready = table.Column<bool>(type: "boolean", nullable: false),
                    in_group_instance = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_level = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_level_passed_trial = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_level_staff_role_id = table.Column<string>(type: "text", nullable: true),
                    overturned_bans = table.Column<bool>(type: "boolean", nullable: false),
                    overturned_bans_max = table.Column<int>(type: "integer", nullable: true),
                    overturned_bans_days = table.Column<int>(type: "integer", nullable: true),
                    daily_ban_cap = table.Column<bool>(type: "boolean", nullable: false),
                    daily_ban_cap_max = table.Column<int>(type: "integer", nullable: true),
                    missed_clips = table.Column<bool>(type: "boolean", nullable: false),
                    missed_clips_max = table.Column<int>(type: "integer", nullable: true),
                    missed_clips_days = table.Column<int>(type: "integer", nullable: true),
                    take_back_user_ids = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automatic_role", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automatic_role");
        }
    }
}
