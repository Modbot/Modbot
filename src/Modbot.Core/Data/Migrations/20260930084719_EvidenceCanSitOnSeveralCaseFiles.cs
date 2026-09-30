using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Lets one evidence file sit on several case files and be taken off one, by moving "which case
    /// file holds it" out of the file's own row into a row of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file's row used to carry one <c>report_id</c>. A second case file that attached the same
    /// bytes found the file "already attached" and showed nothing, and nothing ever cleared the
    /// column, so a file that had once been on a case file could never be destroyed.
    /// </para>
    /// <para>
    /// <strong>Up.</strong> One row in the new table for every file that was on a case file, keeping
    /// the case file, the name it was put on under, the person who put it on (matched to an account
    /// by username, and left as a bare name when no account has that username any more) and the time
    /// the file was first stored, which is when it was put on. A row whose <c>report_id</c> names no
    /// case file is not copied: there is nothing for it to be on. Then the column and its index go.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> The column comes back holding the case file the file is still on. A
    /// file that had been put on several case files keeps only the newest one, and taken-off rows
    /// are lost: the older version has nowhere to keep either.
    /// </para>
    /// </remarks>
    public partial class EvidenceCanSitOnSeveralCaseFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_evidence_attachment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    case_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attached_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attached_by_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    taken_off_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    taken_off_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    taken_off_by_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_evidence_attachment", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_attachment_case",
                table: "modbot_evidence_attachment",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_attachment_hash",
                table: "modbot_evidence_attachment",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ux_evidence_attachment_on",
                table: "modbot_evidence_attachment",
                columns: new[] { "hash", "case_id" },
                unique: true,
                filter: "taken_off_at IS NULL");

            migrationBuilder.Sql(
                "INSERT INTO modbot_evidence_attachment "
                + "(id, hash, case_id, attached_at, attached_by_user_id, attached_by_name, file_name) "
                + "SELECT gen_random_uuid(), b.hash, b.report_id, b.first_stored_at, "
                + "(SELECT u.id FROM modbot_user u WHERE lower(u.username) = lower(b.uploader_id) LIMIT 1), "
                + "left(b.uploader_id, 64), b.file_name "
                + "FROM modbot_evidence_blob b "
                + "JOIN case_file c ON c.id::text = b.report_id "
                + "WHERE b.report_id IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "ix_modbot_evidence_blob_report_id",
                table: "modbot_evidence_blob");

            migrationBuilder.DropColumn(
                name: "report_id",
                table: "modbot_evidence_blob");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "report_id",
                table: "modbot_evidence_blob",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE modbot_evidence_blob b SET report_id = ("
                + "SELECT a.case_id FROM modbot_evidence_attachment a "
                + "WHERE a.hash = b.hash AND a.taken_off_at IS NULL "
                + "ORDER BY a.attached_at DESC LIMIT 1);");

            migrationBuilder.CreateIndex(
                name: "ix_modbot_evidence_blob_report_id",
                table: "modbot_evidence_blob",
                column: "report_id");

            migrationBuilder.DropTable(
                name: "modbot_evidence_attachment");
        }
    }
}
