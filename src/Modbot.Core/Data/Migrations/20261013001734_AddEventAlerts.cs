using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_alerts_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "event_alerts_direct_message",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "event_alerts_minutes_before",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "event_alerts_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "event_alerts_ping",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "event_alert",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_alert", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_alert_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_alert_member",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    way = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_alert_member", x => x.discord_user_id);
                });

            migrationBuilder.CreateTable(
                name: "event_alert_signup",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_alert_signup", x => x.id);
                    table.CheckConstraint("ck_event_alert_signup_one_target", "(category_id IS NULL) <> (event_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_event_alert_signup_calendar_category_category_id",
                        column: x => x.category_id,
                        principalTable: "calendar_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_event_alert_signup_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "event_alert_delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    way = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direct_messages_closed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_alert_delivery", x => x.id);
                    table.ForeignKey(
                        name: "fk_event_alert_delivery_event_alert_alert_id",
                        column: x => x.alert_id,
                        principalTable: "event_alert",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_alert_queued_at",
                table: "event_alert",
                column: "queued_at");

            migrationBuilder.CreateIndex(
                name: "ux_event_alert_event_date",
                table: "event_alert",
                columns: new[] { "event_id", "occurrence_starts_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_alert_delivery_discord_user_id",
                table: "event_alert_delivery",
                column: "discord_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_alert_delivery_waiting",
                table: "event_alert_delivery",
                column: "updated_at",
                filter: "state = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "ux_event_alert_delivery_alert_user",
                table: "event_alert_delivery",
                columns: new[] { "alert_id", "discord_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_alert_signup_category_id",
                table: "event_alert_signup",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_alert_signup_event_id",
                table: "event_alert_signup",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ux_event_alert_signup_category",
                table: "event_alert_signup",
                columns: new[] { "discord_user_id", "category_id" },
                unique: true,
                filter: "category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_event_alert_signup_event",
                table: "event_alert_signup",
                columns: new[] { "discord_user_id", "event_id" },
                unique: true,
                filter: "event_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_alert_delivery");

            migrationBuilder.DropTable(
                name: "event_alert_member");

            migrationBuilder.DropTable(
                name: "event_alert_signup");

            migrationBuilder.DropTable(
                name: "event_alert");

            migrationBuilder.DropColumn(
                name: "event_alerts_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_alerts_direct_message",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_alerts_minutes_before",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_alerts_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_alerts_ping",
                table: "settings");
        }
    }
}
