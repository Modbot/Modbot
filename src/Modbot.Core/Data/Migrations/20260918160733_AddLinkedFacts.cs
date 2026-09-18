using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "link_version",
                table: "modbot_review_run_state",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "modbot_linked_fact",
                columns: table => new
                {
                    fact_id = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    main_fact_id = table.Column<long>(type: "bigint", nullable: false),
                    main_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_linked_fact", x => x.fact_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_linked_fact_main",
                table: "modbot_linked_fact",
                columns: new[] { "main_fact_id", "main_occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_linked_fact_occurred",
                table: "modbot_linked_fact",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_linked_fact");

            migrationBuilder.DropColumn(
                name: "link_version",
                table: "modbot_review_run_state");
        }
    }
}
