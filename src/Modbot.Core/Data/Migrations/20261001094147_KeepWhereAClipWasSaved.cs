using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepWhereAClipWasSaved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "clip_instance_id",
                table: "modbot_evidence_blob",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "clip_saved_at",
                table: "modbot_evidence_blob",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "clip_saved_by_id",
                table: "modbot_evidence_blob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "clip_saved_by_name",
                table: "modbot_evidence_blob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "clip_world_id",
                table: "modbot_evidence_blob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "clip_instance_id",
                table: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "clip_saved_at",
                table: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "clip_saved_by_id",
                table: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "clip_saved_by_name",
                table: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "clip_world_id",
                table: "modbot_evidence_blob");
        }
    }
}
