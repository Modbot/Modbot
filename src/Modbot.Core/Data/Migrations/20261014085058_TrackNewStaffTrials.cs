using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackNewStaffTrials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "new_staff_sign_offs",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "new_staff_tasks",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "staff_trial",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buddy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    buddy_note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    buddy_note_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_trial", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_trial_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trial_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_off_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_off_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_trial_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_trial_item_staff_trial_trial_id",
                        column: x => x.trial_id,
                        principalTable: "staff_trial",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_trial_buddy",
                table: "staff_trial",
                column: "buddy_id");

            migrationBuilder.CreateIndex(
                name: "ux_staff_trial_account",
                table: "staff_trial",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_trial_item_trial_id",
                table: "staff_trial_item",
                column: "trial_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_trial_item");

            migrationBuilder.DropTable(
                name: "staff_trial");

            migrationBuilder.DropColumn(
                name: "new_staff_sign_offs",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "new_staff_tasks",
                table: "settings");
        }
    }
}
