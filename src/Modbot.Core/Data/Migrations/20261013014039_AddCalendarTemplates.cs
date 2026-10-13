using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_template",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    cover_picture_id = table.Column<Guid>(type: "uuid", nullable: true),
                    world_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_template", x => x.id);
                    table.ForeignKey(
                        name: "fk_calendar_template_calendar_cover_picture_cover_picture_id",
                        column: x => x.cover_picture_id,
                        principalTable: "calendar_cover_picture",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_calendar_template_world_lists_world_list_id",
                        column: x => x.world_list_id,
                        principalTable: "world_list",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_template_cover_picture_id",
                table: "calendar_template",
                column: "cover_picture_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_template_name",
                table: "calendar_template",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_template_world_list_id",
                table: "calendar_template",
                column: "world_list_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_template");
        }
    }
}
