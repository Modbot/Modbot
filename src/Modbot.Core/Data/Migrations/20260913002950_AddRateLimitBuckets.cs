using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rate_limit_bucket",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    endpoint_class = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    ceiling_per_second = table.Column<double>(type: "double precision", nullable: false),
                    fraction = table.Column<double>(type: "double precision", nullable: false),
                    budget_multiplier = table.Column<double>(type: "double precision", nullable: false),
                    tokens = table.Column<double>(type: "double precision", nullable: false),
                    tokens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stopped_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    probe_in_flight = table.Column<bool>(type: "boolean", nullable: false),
                    consecutive_probe_failures = table.Column<int>(type: "integer", nullable: false),
                    alerting = table.Column<bool>(type: "boolean", nullable: false),
                    last_adapted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_rate_limited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rate_limit_hits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_bucket", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rate_limit_bucket_stopped",
                table: "rate_limit_bucket",
                column: "stopped_until");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_limit_bucket");
        }
    }
}
