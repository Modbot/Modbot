using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds the <c>member_report</c> table (Discord commands design §3.4), the setting that says how
    /// long a closed report keeps its words, and gives the built-in Moderator role the two new
    /// permissions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Up.</strong> The retention setting starts at 365 days for the row that already exists
    /// (decision 10: one year), not at 0, which would keep every word forever. The Moderator role gets
    /// See reports and Handle reports (decision 1); roles an operator made themselves are left alone,
    /// and Administrator holds everything already.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> The two bits come off every role, the table and the setting go. The
    /// older version does not know the bits and ignores them.
    /// </para>
    /// </remarks>
    public partial class LetMembersReportToTheMods : Migration
    {
        /// <summary><c>ModbotPermissions.ViewReports</c> and <c>HandleReports</c>, bits 57 and 58. Written out, so this migration never changes meaning.</summary>
        private const long ReportPermissions = (1L << 57) | (1L << 58);

        private const string ModeratorRole = "00000000-0000-0000-0000-000000000002";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "member_report_retention_days",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.Sql(
                $"UPDATE modbot_role SET permissions = permissions | {ReportPermissions} WHERE id = '{ModeratorRole}';");

            migrationBuilder.CreateTable(
                name: "member_report",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_discord_id = table.Column<string>(type: "text", nullable: false),
                    reporter_name = table.Column<string>(type: "text", nullable: false),
                    reported_discord_id = table.Column<string>(type: "text", nullable: false),
                    reported_name = table.Column<string>(type: "text", nullable: true),
                    reported_vrchat_user_id = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    message_channel_id = table.Column<string>(type: "text", nullable: true),
                    message_channel_name = table.Column<string>(type: "text", nullable: true),
                    message_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    message_text = table.Column<string>(type: "text", nullable: true),
                    message_attachments = table.Column<string>(type: "jsonb", nullable: true),
                    message_url = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    close_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    text_removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_report", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_member_report_removable",
                table: "member_report",
                column: "closed_at",
                filter: "state = 'closed' AND text_removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_member_report_reported",
                table: "member_report",
                column: "reported_discord_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_report_reporter",
                table: "member_report",
                columns: new[] { "reporter_discord_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_member_report_state",
                table: "member_report",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_member_report_message",
                table: "member_report",
                columns: new[] { "reporter_discord_id", "message_id" },
                unique: true,
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_member_report_open",
                table: "member_report",
                columns: new[] { "reporter_discord_id", "reported_discord_id" },
                unique: true,
                filter: "state = 'open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE modbot_role SET permissions = permissions & ~{ReportPermissions};");

            migrationBuilder.DropTable(
                name: "member_report");

            migrationBuilder.DropColumn(
                name: "member_report_retention_days",
                table: "settings");
        }
    }
}
