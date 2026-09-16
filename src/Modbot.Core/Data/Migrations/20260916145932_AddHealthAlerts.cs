using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The emails Modbot sends about its own health: what it watches, what each check last said,
    /// and which staff accounts are told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state lives in the database rather than in memory so a restart in the middle of a
    /// problem does not start the whole thing over and send the same email again -- which is the
    /// failure mode that makes people turn alerting off.
    /// </para>
    /// <para>
    /// <c>check_name</c> and <c>watched</c> are named by hand: the convention would give
    /// <c>check</c> and <c>on</c>, both reserved words in PostgreSQL. EF quotes them and it works;
    /// hand-written SQL against this table would not, and one day somebody writes some.
    /// </para>
    /// </remarks>
    public partial class AddHealthAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_health_alert_recipient",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_health_alert_recipient", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_modbot_health_alert_recipient_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "modbot_health_alert_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    quiet_hours = table.Column<int>(type: "integer", nullable: false),
                    storage_warn_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_health_alert_settings", x => x.id);
                    table.CheckConstraint("ck_modbot_health_alert_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "modbot_health_watch",
                columns: table => new
                {
                    check_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    watched = table.Column<bool>(type: "boolean", nullable: false),
                    problem = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_health_watch", x => x.check_name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_health_alert_recipient");

            migrationBuilder.DropTable(
                name: "modbot_health_alert_settings");

            migrationBuilder.DropTable(
                name: "modbot_health_watch");
        }
    }
}
