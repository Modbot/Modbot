using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LeaveHeadsUpsForStaffInTheSameInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "heads_up",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: true),
                    subject_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    text = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    place = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    placed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_by_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    placed_by_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cleared_by_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cleared_because = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_heads_up", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_heads_up_standing",
                table: "heads_up",
                column: "instance_id",
                filter: "cleared_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "heads_up");
        }
    }
}
