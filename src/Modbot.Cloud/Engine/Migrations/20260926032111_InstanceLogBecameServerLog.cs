using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Engine.Migrations
{
    /// <summary>
    /// The log lines a Modbot server sends Cloud, under the name "server" rather than "instance",
    /// which is left to VRChat's own meaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written SQL, as the table itself was. The scaffolder proposed dropping the table and
    /// making a new one, which would have thrown away every line held; and even as renames, EF Core
    /// knows nothing of the monthly partitions under the table or the sequence behind its id.
    /// </para>
    /// <para>
    /// <strong>Every month is renamed with the table</strong>, found by asking PostgreSQL which
    /// partitions the table has rather than by listing the months this migration expects, because
    /// which months exist depends on when a database was running. <c>LogPartitionMaintainer</c>
    /// makes new months under the new name. <c>LogRetention</c> still recognises the old name, so a
    /// month left under it is dropped when due rather than kept forever.
    /// </para>
    /// <para>
    /// The id's default names its sequence by its internal id, not by its name, so renaming the
    /// sequence leaves the default pointing at it.
    /// </para>
    /// </remarks>
    public partial class InstanceLogBecameServerLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE instance_log RENAME TO server_log;");
            migrationBuilder.Sql("ALTER SEQUENCE instance_log_id_seq RENAME TO server_log_id_seq;");
            migrationBuilder.Sql("ALTER TABLE server_log RENAME CONSTRAINT pk_instance_log TO pk_server_log;");
            migrationBuilder.Sql("ALTER INDEX ix_instance_log_server_received_at RENAME TO ix_server_log_server_received_at;");
            migrationBuilder.Sql("ALTER INDEX ix_instance_log_level_received_at RENAME TO ix_server_log_level_received_at;");

            migrationBuilder.Sql(RenameMonths(from: "instance_log", to: "server_log", parent: "server_log"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameMonths(from: "server_log", to: "instance_log", parent: "server_log"));

            migrationBuilder.Sql("ALTER INDEX ix_server_log_level_received_at RENAME TO ix_instance_log_level_received_at;");
            migrationBuilder.Sql("ALTER INDEX ix_server_log_server_received_at RENAME TO ix_instance_log_server_received_at;");
            migrationBuilder.Sql("ALTER TABLE server_log RENAME CONSTRAINT pk_server_log TO pk_instance_log;");
            migrationBuilder.Sql("ALTER SEQUENCE server_log_id_seq RENAME TO instance_log_id_seq;");
            migrationBuilder.Sql("ALTER TABLE server_log RENAME TO instance_log;");
        }

        /// <summary>
        /// Renames every monthly partition of <paramref name="parent"/> named
        /// <c>{from}_YYYY_MM</c> to <c>{to}_YYYY_MM</c>, and the indexes PostgreSQL made on each
        /// month, whose names start with the month's own (<c>instance_log_2026_09_pkey</c> and so on).
        /// Renaming the index behind a primary key renames the key with it.
        /// </summary>
        private static string RenameMonths(string from, string to, string parent) => $$"""
            DO $$
            DECLARE
                month record;
                ix record;
            BEGIN
                FOR month IN
                    SELECT child.oid, child.relname
                    FROM pg_inherits
                    JOIN pg_class child ON child.oid = pg_inherits.inhrelid
                    WHERE pg_inherits.inhparent = '{{parent}}'::regclass
                      AND child.relname ~ '^{{from}}_\d{4}_\d{2}$'
                LOOP
                    FOR ix IN
                        SELECT i.oid, i.relname
                        FROM pg_index x
                        JOIN pg_class i ON i.oid = x.indexrelid
                        WHERE x.indrelid = month.oid
                          AND left(i.relname, length('{{from}}_')) = '{{from}}_'
                    LOOP
                        EXECUTE format('ALTER INDEX %s RENAME TO %I',
                            ix.oid::regclass, '{{to}}_' || substr(ix.relname, length('{{from}}_') + 1));
                    END LOOP;

                    EXECUTE format('ALTER TABLE %s RENAME TO %I',
                        month.oid::regclass, '{{to}}_' || substr(month.relname, length('{{from}}_') + 1));
                END LOOP;
            END
            $$;
            """;
    }
}
