using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds the <c>feedback</c> table (feedback design §3) and gives the built-in Moderator role the
    /// new Send feedback permission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Up.</strong> Moderator gets Send feedback (decision: all staff may send); roles an
    /// operator made themselves are left alone, and Administrator holds everything already. Review
    /// feedback is in no built-in role.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> Both bits come off every role and the table goes. The older version does
    /// not know the bits and ignores them.
    /// </para>
    /// </remarks>
    public partial class AddFeedback : Migration
    {
        /// <summary><c>ModbotPermissions.SendFeedback</c>, bit 61. Written out, so this migration never changes meaning.</summary>
        private const long SendFeedbackBit = 1L << 61;

        /// <summary><c>ModbotPermissions.ReviewFeedback</c>, bit 34.</summary>
        private const long ReviewFeedbackBit = 1L << 34;

        private const string ModeratorRole = "00000000-0000-0000-0000-000000000002";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE modbot_role SET permissions = permissions | {SendFeedbackBit} WHERE id = '{ModeratorRole}';");

            migrationBuilder.CreateTable(
                name: "feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feedback", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feedback_status",
                table: "feedback",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE modbot_role SET permissions = permissions & ~{SendFeedbackBit | ReviewFeedbackBit};");

            migrationBuilder.DropTable(
                name: "feedback");
        }
    }
}
