using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomHeadCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "head_count",
                table: "vrchat_instance",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "head_count_source",
                table: "vrchat_instance",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "page_checked_at",
                table: "vrchat_instance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "page_read_at",
                table: "vrchat_instance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_user_count",
                table: "vrchat_instance",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "instance_head_count",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    head_count = table.Column<int>(type: "integer", nullable: false),
                    user_count = table.Column<int>(type: "integer", nullable: true),
                    member_count = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_head_count", x => x.id);
                    table.ForeignKey(
                        name: "fk_instance_head_count_vr_chat_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "vrchat_instance",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_instance_head_count_room",
                table: "instance_head_count",
                columns: new[] { "instance_id", "counted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_head_count");

            migrationBuilder.DropColumn(
                name: "head_count",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "head_count_source",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "page_checked_at",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "page_read_at",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "page_user_count",
                table: "vrchat_instance");
        }
    }
}
