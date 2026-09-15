using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSpendLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_model_price",
                columns: table => new
                {
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    cached_input_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    output_per_million = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_model_price", x => x.model);
                });

            migrationBuilder.CreateTable(
                name: "ai_spend_limit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    applies_to = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    per_day = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    per_month = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_spend_limit", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_spend_limit_modbot_role_role_id",
                        column: x => x.role_id,
                        principalTable: "modbot_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_spend_limit_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_user_at",
                table: "ai_usage",
                columns: new[] { "user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_spend_limit_role_id",
                table: "ai_spend_limit",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_spend_limit_user_id",
                table: "ai_spend_limit",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_model_price");

            migrationBuilder.DropTable(
                name: "ai_spend_limit");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_user_at",
                table: "ai_usage");
        }
    }
}
