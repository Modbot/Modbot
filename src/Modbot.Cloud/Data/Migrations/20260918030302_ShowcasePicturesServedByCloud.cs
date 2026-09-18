using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowcasePicturesServedByCloud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "saved_group_banner_id",
                table: "showcase_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "saved_group_image_id",
                table: "showcase_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "saved_image_id",
                table: "showcase_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "showcase_picture",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_showcase_picture", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "showcase_picture");

            migrationBuilder.DropColumn(
                name: "saved_group_banner_id",
                table: "showcase_entry");

            migrationBuilder.DropColumn(
                name: "saved_group_image_id",
                table: "showcase_entry");

            migrationBuilder.DropColumn(
                name: "saved_image_id",
                table: "showcase_entry");
        }
    }
}
