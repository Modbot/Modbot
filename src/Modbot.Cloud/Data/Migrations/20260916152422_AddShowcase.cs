using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// The sponsors and early adopters every Modbot shows on its Credits page.
    /// </summary>
    /// <remarks>
    /// One table for both kinds, with a word saying which. They are the same row -- a name, a link,
    /// a picture, and for a VRChat group its id and two images -- and two tables would be the same
    /// columns twice to say "sponsor" instead of "early adopter".
    ///
    /// Contributors are not here: GitHub already knows who they are, and a second list kept by hand
    /// only goes out of date.
    /// </remarks>
    public partial class AddShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "showcase_entry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    vr_chat_group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    group_image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    group_banner_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_showcase_entry", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_showcase_entry_kind_order",
                table: "showcase_entry",
                columns: new[] { "kind", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "showcase_entry");
        }
    }
}
