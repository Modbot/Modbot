using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    onboarding_complete = table.Column<bool>(type: "boolean", nullable: false),
                    vr_chat_username = table.Column<string>(type: "text", nullable: true),
                    vr_chat_password_encrypted = table.Column<string>(type: "text", nullable: true),
                    vr_chat_totp_secret_encrypted = table.Column<string>(type: "text", nullable: true),
                    vr_chat_auth_cookie_encrypted = table.Column<string>(type: "text", nullable: true),
                    managed_group_id = table.Column<string>(type: "text", nullable: true),
                    managed_group_name = table.Column<string>(type: "text", nullable: true),
                    proxy_url = table.Column<string>(type: "text", nullable: true),
                    proxy_username = table.Column<string>(type: "text", nullable: true),
                    proxy_password_encrypted = table.Column<string>(type: "text", nullable: true),
                    moderation_fact_retention_days = table.Column<int>(type: "integer", nullable: false),
                    presence_fact_retention_days = table.Column<int>(type: "integer", nullable: false),
                    dedup_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    require_moderation_classification = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                    table.CheckConstraint("ck_settings_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settings");
        }
    }
}
