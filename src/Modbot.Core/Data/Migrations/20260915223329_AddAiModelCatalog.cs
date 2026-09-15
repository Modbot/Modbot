using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_catalog_model",
                columns: table => new
                {
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    maker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    context_length = table.Column<int>(type: "integer", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    input_modalities = table.Column<string>(type: "jsonb", nullable: false),
                    output_modalities = table.Column<string>(type: "jsonb", nullable: false),
                    supported_parameters = table.Column<string>(type: "jsonb", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    cached_input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    output_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    price_varies = table.Column<bool>(type: "boolean", nullable: false),
                    prices = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_catalog_model", x => x.model);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_catalog_model_maker",
                table: "ai_catalog_model",
                column: "maker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_catalog_model");
        }
    }
}
