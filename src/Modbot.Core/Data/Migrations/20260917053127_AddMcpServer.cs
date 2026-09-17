using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The MCP server (MCP server design): its on/off switch on <c>settings</c>, and the three
    /// tables behind its sign-in -- the AI apps that registered, the codes a person approved, and
    /// each person's connections with their hashed tokens.
    /// </summary>
    public partial class AddMcpServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "mcp_server_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mcp_authorization_code",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_authorization_code", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_client",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    redirect_uris = table.Column<string>(type: "jsonb", nullable: false),
                    client_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_client", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_grant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    access_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refresh_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    refresh_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_grant", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_grant_mcp_client_client_id",
                        column: x => x.client_id,
                        principalTable: "mcp_client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_authorization_code_code_hash",
                table: "mcp_authorization_code",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_grant_access_token_hash",
                table: "mcp_grant",
                column: "access_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_grant_client_id",
                table: "mcp_grant",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_grant_refresh_token_hash",
                table: "mcp_grant",
                column: "refresh_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_grant_user_id",
                table: "mcp_grant",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_authorization_code");

            migrationBuilder.DropTable(
                name: "mcp_grant");

            migrationBuilder.DropTable(
                name: "mcp_client");

            migrationBuilder.DropColumn(
                name: "mcp_server_enabled",
                table: "settings");
        }
    }
}
