using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cloud_last_report_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cloud_last_report_ok",
                table: "settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_last_report_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_server_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_server_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cloud_last_report_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_last_report_ok",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_last_report_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_server_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_server_secret_encrypted",
                table: "settings");
        }
    }
}
