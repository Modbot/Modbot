using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appeal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    received_on = table.Column<DateOnly>(type: "date", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    case_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    logged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    logged_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logged_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appeal", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appeal_person",
                table: "appeal",
                columns: new[] { "user_id", "received_on" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_appeal_received_on",
                table: "appeal",
                column: "received_on");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appeal");
        }
    }
}
