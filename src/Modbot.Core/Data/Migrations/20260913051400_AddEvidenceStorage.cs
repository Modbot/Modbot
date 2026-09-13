using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "evidence_backend",
                table: "settings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<bool>(
                name: "evidence_direct_delivery_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "evidence_disk_acknowledged",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "evidence_disk_acknowledged_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_disk_acknowledged_by",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_disk_warning_shown",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "evidence_max_deployment_bytes",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "evidence_max_file_bytes",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "evidence_max_report_bytes",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "evidence_root",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3access_key_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3bucket",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3endpoint",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3prefix",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3region",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_s3secret_access_key_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "evidence_s3use_path_style",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "evidence_store_id",
                table: "settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "modbot_evidence_blob",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    backend = table.Column<short>(type: "smallint", nullable: false),
                    first_stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    uploader_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    report_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    destroyed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    destroyed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    destroyed_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_evidence_blob", x => x.hash);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_evidence_blob_report_id",
                table: "modbot_evidence_blob",
                column: "report_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "evidence_backend",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_direct_delivery_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_disk_acknowledged",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_disk_acknowledged_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_disk_acknowledged_by",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_disk_warning_shown",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_max_deployment_bytes",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_max_file_bytes",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_max_report_bytes",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_root",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3access_key_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3bucket",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3endpoint",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3prefix",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3region",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3secret_access_key_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_s3use_path_style",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "evidence_store_id",
                table: "settings");
        }
    }
}
