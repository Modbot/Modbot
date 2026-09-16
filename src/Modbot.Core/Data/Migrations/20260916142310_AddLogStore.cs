using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Modbot's own log, in its own database, so it can be read in the app without Seq and without
    /// a disk that survives a redeploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not partitioned, unlike the fact log and the stored Discord messages. Those run to hundreds
    /// of millions of rows and can only be pruned by dropping whole months; this table leaves out
    /// outbound API traffic, so six months of it is a few million rows at most and a sliced delete
    /// is the simpler thing that works. If it ever grows past that, partition it by <c>at</c>.
    /// </para>
    /// <para>
    /// The settings columns beside it are the retention window, the switch for sending the same
    /// lines to Modbot Cloud, and the shipper's own place-marker and counters.
    /// </para>
    /// </remarks>
    public partial class AddLogStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cloud_log_dropped",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "cloud_log_error",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cloud_log_error_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_log_install_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cloud_log_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cloud_log_sent_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cloud_log_sent_through_id",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "log_retention_days",
                table: "settings",
                type: "integer",
                nullable: false,
                // 180, not EF's 0: zero means "keep forever" here, and a deployment that upgrades
                // into this feature should get the six-month window that was chosen for it rather
                // than a table nothing ever prunes.
                defaultValue: 180);

            migrationBuilder.AddColumn<bool>(
                name: "ship_logs_to_cloud",
                table: "settings",
                type: "boolean",
                nullable: false,
                // On, like the C# default. Sending logs to Cloud is on unless somebody turns it
                // off, and MODBOT_CLOUD_DISABLED stops it whatever this says.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "modbot_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    area = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    exception = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_log_at",
                table: "modbot_log",
                column: "at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_log_level_at",
                table: "modbot_log",
                columns: new[] { "level", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_log_source_at",
                table: "modbot_log",
                columns: new[] { "source", "at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_log");

            migrationBuilder.DropColumn(
                name: "cloud_log_dropped",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_error",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_error_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_install_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_secret_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_sent_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "cloud_log_sent_through_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "log_retention_days",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ship_logs_to_cloud",
                table: "settings");
        }
    }
}
