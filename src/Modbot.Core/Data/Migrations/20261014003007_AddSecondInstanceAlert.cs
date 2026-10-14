using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecondInstanceAlert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "second_instance_at_people",
                table: "modbot_alert_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "second_instance_at_percent",
                table: "modbot_alert_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "instance_id",
                table: "modbot_alert",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "second_location",
                table: "modbot_alert",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "second_opened_by_user_id",
                table: "modbot_alert",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "second_tried_at",
                table: "modbot_alert",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_modbot_alert_instance",
                table: "modbot_alert",
                column: "instance_id",
                unique: true,
                filter: "instance_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_modbot_alert_instance",
                table: "modbot_alert");

            migrationBuilder.DropColumn(
                name: "second_instance_at_people",
                table: "modbot_alert_settings");

            migrationBuilder.DropColumn(
                name: "second_instance_at_percent",
                table: "modbot_alert_settings");

            migrationBuilder.DropColumn(
                name: "instance_id",
                table: "modbot_alert");

            migrationBuilder.DropColumn(
                name: "second_location",
                table: "modbot_alert");

            migrationBuilder.DropColumn(
                name: "second_opened_by_user_id",
                table: "modbot_alert");

            migrationBuilder.DropColumn(
                name: "second_tried_at",
                table: "modbot_alert");
        }
    }
}
