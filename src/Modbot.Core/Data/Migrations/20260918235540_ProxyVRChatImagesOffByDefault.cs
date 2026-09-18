using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProxyVRChatImagesOffByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column's own default, not the property's. EF generated nothing here because a
            // CLR initialiser is not part of the model's schema -- and leaving it at that would
            // have changed nothing at all, since a database default beats the value EF sends when
            // that value is the type's own default. A bool set to false is exactly that case, so
            // the row would have gone on being written as true.
            migrationBuilder.AlterColumn<bool>(
                name: "vr_chat_images_proxied",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: false,
                oldDefaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "vr_chat_images_proxied",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: false,
                oldDefaultValue: false);
        }
    }
}
