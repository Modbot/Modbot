using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldHeadCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "world_head_count",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occupants = table.Column<int>(type: "integer", nullable: true),
                    public_occupants = table.Column<int>(type: "integer", nullable: true),
                    private_occupants = table.Column<int>(type: "integer", nullable: true),
                    instances = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_world_head_count", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_world_head_count_world",
                table: "world_head_count",
                columns: new[] { "world_id", "counted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "world_head_count");
        }
    }
}
