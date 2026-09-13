using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds the rollup table (spec 5.4) and the incremental job's watermark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generic on purpose: metric and dimension are text, so a metric invented next year is a
    /// registry entry and a rebuild rather than a migration.
    /// </para>
    /// <para>
    /// Not partitioned, unlike the fact log. Rollups are kept forever (spec 5.5) and there is one
    /// row per day per metric per dimension -- a rounding error next to the facts they summarise.
    /// </para>
    /// </remarks>
    public partial class AddRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_rollup_daily",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    dimension = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_rollup_daily", x => new { x.day, x.metric, x.dimension });
                });

            migrationBuilder.CreateTable(
                name: "modbot_rollup_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    observed_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_rollup_state", x => x.id);
                    table.CheckConstraint("ck_modbot_rollup_state_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_rollup_daily_metric",
                table: "modbot_rollup_daily",
                columns: new[] { "metric", "day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_rollup_daily");

            migrationBuilder.DropTable(
                name: "modbot_rollup_state");
        }
    }
}
