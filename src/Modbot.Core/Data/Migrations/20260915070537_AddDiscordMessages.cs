using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Stores the Discord server's messages in full, their earlier texts, and how far back each
    /// channel has been read (M5 spec §5.1, changed 2026-09-15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>discord_message</c> and <c>discord_message_edit</c> are hand-written SQL, like the fact
    /// log, because EF Core cannot express declarative partitioning. The model in
    /// <c>ModbotContext</c> describes exactly this shape, so the snapshot stays accurate; if you
    /// change the columns here, change them there too.
    /// </para>
    /// <para>
    /// Both are partitioned by month on the message's send time, so retention drops a month of
    /// messages and their edits together. <strong>No partitions are created here</strong>: reading
    /// back reaches years into the past, so <c>MessagePartitionMaintainer</c> creates each month the
    /// first time a message from it is stored.
    /// </para>
    /// </remarks>
    public partial class AddDiscordMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "discord_message_retention_days",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                CREATE TABLE discord_message (
                    message_id    text        NOT NULL,
                    sent_at       timestamptz NOT NULL,
                    guild_id      text        NOT NULL,
                    channel_id    text        NOT NULL,
                    thread_id     text        NULL,
                    author_id     text        NOT NULL,
                    author_name   text        NOT NULL,
                    author_is_bot boolean     NOT NULL,
                    text          text        NOT NULL,
                    attachments   jsonb       NOT NULL,
                    embed_count   integer     NOT NULL,
                    reply_to_id   text        NULL,
                    mention_count integer     NOT NULL,
                    pinned        boolean     NOT NULL,
                    edited_at     timestamptz NULL,
                    deleted_at    timestamptz NULL,
                    stored_at     timestamptz NOT NULL,
                    CONSTRAINT pk_discord_message PRIMARY KEY (message_id, sent_at)
                ) PARTITION BY RANGE (sent_at);
                """);

            // On the parent, so every month's partition inherits them.
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_id ON discord_message (message_id);");
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_channel ON discord_message (channel_id, sent_at DESC);");
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_thread ON discord_message (thread_id, sent_at DESC) WHERE thread_id IS NOT NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_author ON discord_message (author_id, sent_at DESC);");
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_stored ON discord_message (stored_at);");

            // A plain sequence default, as on modbot_event: identity columns are not accepted on a
            // partitioned parent in PostgreSQL 16.
            migrationBuilder.Sql("CREATE SEQUENCE discord_message_edit_id_seq AS bigint;");

            migrationBuilder.Sql("""
                CREATE TABLE discord_message_edit (
                    id          bigint      NOT NULL DEFAULT nextval('discord_message_edit_id_seq'),
                    sent_at     timestamptz NOT NULL,
                    message_id  text        NOT NULL,
                    text        text        NOT NULL,
                    replaced_at timestamptz NOT NULL,
                    CONSTRAINT pk_discord_message_edit PRIMARY KEY (id, sent_at)
                ) PARTITION BY RANGE (sent_at);
                """);

            migrationBuilder.Sql("ALTER SEQUENCE discord_message_edit_id_seq OWNED BY discord_message_edit.id;");
            migrationBuilder.Sql("CREATE INDEX ix_discord_message_edit_message ON discord_message_edit (message_id, replaced_at);");

            migrationBuilder.CreateTable(
                name: "discord_read_back",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    parent_channel_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    oldest_read_id = table.Column<string>(type: "text", nullable: true),
                    stored_pages_in_a_row = table.Column<int>(type: "integer", nullable: false),
                    pages_read = table.Column<int>(type: "integer", nullable: false),
                    messages_stored = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_because = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_read_back", x => x.channel_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_read_back_guild",
                table: "discord_read_back",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CASCADE takes the monthly partitions with each table.
            migrationBuilder.Sql("DROP TABLE IF EXISTS discord_message CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS discord_message_edit CASCADE;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS discord_message_edit_id_seq;");

            migrationBuilder.DropTable(
                name: "discord_read_back");

            migrationBuilder.DropColumn(
                name: "discord_message_retention_days",
                table: "settings");
        }
    }
}
