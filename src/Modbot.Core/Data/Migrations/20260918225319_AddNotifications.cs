using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    same_as = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    link = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    first_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    repeats = table.Column<int>(type: "integer", nullable: false),
                    nobody_could_receive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_choice",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    daily_summary = table.Column<bool>(type: "boolean", nullable: false),
                    summary_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_choice", x => new { x.user_id, x.channel });
                    table.ForeignKey(
                        name: "fk_notification_choice_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    quiet_hours = table.Column<int>(type: "integer", nullable: false),
                    on = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_settings", x => x.id);
                    table.CheckConstraint("ck_notification_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "notification_person",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    waiting = table.Column<bool>(type: "boolean", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_person", x => new { x.notification_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_notification_person_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_person_notification_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_send",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_send", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_send_notification_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_same_as",
                table: "notification",
                columns: new[] { "same_as", "last_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_person_waiting",
                table: "notification_person",
                columns: new[] { "user_id", "waiting", "seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_send_for",
                table: "notification_send",
                columns: new[] { "notification_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_send_state",
                table: "notification_send",
                columns: new[] { "state", "queued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_choice");

            migrationBuilder.DropTable(
                name: "notification_person");

            migrationBuilder.DropTable(
                name: "notification_send");

            migrationBuilder.DropTable(
                name: "notification_settings");

            migrationBuilder.DropTable(
                name: "notification");
        }
    }
}
