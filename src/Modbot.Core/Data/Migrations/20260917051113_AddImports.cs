using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds <c>import</c> (one row per upload of old data, holding the upload's bytes until the
    /// job has read them) and <c>import_record</c> (which records are already in, keyed per
    /// source label so a re-upload writes nothing twice). Import design §7. A side table rather
    /// than a unique index on <c>modbot_event</c>, because the log is partitioned and PostgreSQL
    /// requires the partition key in every unique constraint on it.
    /// </summary>
    public partial class AddImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    received = table.Column<int>(type: "integer", nullable: false),
                    imported = table.Column<int>(type: "integer", nullable: false),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    rejections = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    body = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_record",
                columns: table => new
                {
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fact_id = table.Column<long>(type: "bigint", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_platform = table.Column<short>(type: "smallint", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_record", x => new { x.source, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_status",
                table: "import",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_record_subject",
                table: "import_record",
                columns: new[] { "subject_platform", "subject_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import");

            migrationBuilder.DropTable(
                name: "import_record");
        }
    }
}
