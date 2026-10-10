using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// 2026-10-09: VRChat refused an event it holds as featured when Modbot wrote it back with
    /// Featured on, and refused every event with the box ticked on an account that may not feature.
    /// Two columns: what VRChat itself said about Featured for an event read from its calendar, so
    /// a write can tell a moderator's change from VRChat's own value, and when VRChat last refused
    /// the account for Featured.
    /// </summary>
    public partial class LeaveFeaturedAloneOnEventsMadeOnVRChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_featured_refused_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "vrchat_featured",
                table: "calendar_event",
                type: "boolean",
                nullable: true);

            // An event made on VRChat has the Featured VRChat gave it, unless a moderator changed
            // it since, which could not be told apart before this column: taken as VRChat's.
            migrationBuilder.Sql("UPDATE calendar_event SET vrchat_featured = featured WHERE made_on_vrchat;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "vr_chat_featured_refused_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "vrchat_featured",
                table: "calendar_event");
        }
    }
}
