using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The searchable form of every display name and username, beside the name (NameNormalizer),
    /// and the version of the rules that made them, so a later change re-makes them all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The columns are filled by the name catch-up after startup, not here: the folding is C#,
    /// not SQL, and a migration that walked 150,000 rows would hold every deployment at the door.
    /// </para>
    /// <para>
    /// The trigram indexes are what the foundation design asks for (§6.4, <c>pg_trgm</c> with GIN)
    /// and are the only thing that makes <c>ILIKE '%term%'</c> over a large table not a scan.
    /// They are created only where the extension can be: a host whose database user may not
    /// create extensions still gets the columns and the search, only slower, rather than a
    /// migration that fails at startup and takes the whole server down with it.
    /// </para>
    /// </remarks>
    public partial class SearchableNames : Migration
    {
        private const string CreateTrigramIndexes = """
            DO $$
            BEGIN
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'pg_trgm is not available here; name search runs without trigram indexes';
                END;

                IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                    CREATE INDEX IF NOT EXISTS ix_vrchat_user_display_name_searchable_trgm
                        ON vrchat_user USING gin (display_name_searchable gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_vrchat_user_display_name_trgm
                        ON vrchat_user USING gin (display_name gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_discord_member_username_searchable_trgm
                        ON discord_member USING gin (username_searchable gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_discord_member_display_name_searchable_trgm
                        ON discord_member USING gin (display_name_searchable gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_discord_member_global_name_searchable_trgm
                        ON discord_member USING gin (global_name_searchable gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_discord_member_nickname_searchable_trgm
                        ON discord_member USING gin (nickname_searchable gin_trgm_ops);
                END IF;
            END $$;
            """;

        private const string DropTrigramIndexes = """
            DROP INDEX IF EXISTS ix_vrchat_user_display_name_searchable_trgm;
            DROP INDEX IF EXISTS ix_vrchat_user_display_name_trgm;
            DROP INDEX IF EXISTS ix_discord_member_username_searchable_trgm;
            DROP INDEX IF EXISTS ix_discord_member_display_name_searchable_trgm;
            DROP INDEX IF EXISTS ix_discord_member_global_name_searchable_trgm;
            DROP INDEX IF EXISTS ix_discord_member_nickname_searchable_trgm;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "display_name_searchable",
                table: "vrchat_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "name_catch_up_version",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "display_name_searchable",
                table: "discord_member",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "global_name_searchable",
                table: "discord_member",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nickname_searchable",
                table: "discord_member",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "username_searchable",
                table: "discord_member",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(CreateTrigramIndexes);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropTrigramIndexes);

            migrationBuilder.DropColumn(
                name: "display_name_searchable",
                table: "vrchat_user");

            migrationBuilder.DropColumn(
                name: "name_catch_up_version",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "display_name_searchable",
                table: "discord_member");

            migrationBuilder.DropColumn(
                name: "global_name_searchable",
                table: "discord_member");

            migrationBuilder.DropColumn(
                name: "nickname_searchable",
                table: "discord_member");

            migrationBuilder.DropColumn(
                name: "username_searchable",
                table: "discord_member");
        }
    }
}
