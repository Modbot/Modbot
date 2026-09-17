using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpClientDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "metadata_fetched_at",
                table: "mcp_client",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata_url",
                table: "mcp_client",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_client_metadata_url",
                table: "mcp_client",
                column: "metadata_url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_mcp_client_metadata_url",
                table: "mcp_client");

            migrationBuilder.DropColumn(
                name: "metadata_fetched_at",
                table: "mcp_client");

            migrationBuilder.DropColumn(
                name: "metadata_url",
                table: "mcp_client");
        }
    }
}
