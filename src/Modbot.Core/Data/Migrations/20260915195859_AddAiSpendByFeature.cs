using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSpendByFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "reported_cost",
                table: "ai_usage",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "feature",
                table: "ai_spend_limit",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_fetched_price",
                columns: table => new
                {
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    cached_input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    output_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_fetched_price", x => x.model);
                });

            migrationBuilder.CreateTable(
                name: "ai_limit_reached",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_limit_reached", x => new { x.key, x.period_start });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_fetched_price");

            migrationBuilder.DropTable(
                name: "ai_limit_reached");

            migrationBuilder.DropColumn(
                name: "reported_cost",
                table: "ai_usage");

            migrationBuilder.DropColumn(
                name: "feature",
                table: "ai_spend_limit");
        }
    }
}
