using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The Discord bot's moderation log channel (foundation §9): which channel, which event
    /// types, and the poster's cursor -- all on the settings row.
    /// </summary>
    /// <remarks>
    /// The cursor is a fact id, like the profile sync's, and starts null, which the poster reads
    /// as "not started": it jumps to the newest fact and posts nothing, so turning the channel on
    /// never replays the group's history into Discord.
    /// </remarks>
    public partial class AddDiscordModerationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_log_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_log_event_types",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "discord_log_posted_through",
                table: "settings",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_log_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_log_event_types",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_log_posted_through",
                table: "settings");
        }
    }
}
