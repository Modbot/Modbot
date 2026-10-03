using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Bluesky, step 3a of the posts design (§4.2c): the Settings topic's account, app password,
    /// session, Check, Posting switch, the stop after a rate limit and the sign-in guard; and on
    /// <c>post_destination</c>, the site's own copy of the picture (Bluesky's small card picture), a
    /// real column so the hourly picture sweep keeps it. Posting is off to start: nothing goes to
    /// Bluesky until an account passed Check and a person turns it on.
    /// </summary>
    public partial class PostToBluesky : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_app_password_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_automated",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_did",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_display_name",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_handle",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_posting_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_server",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_session_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_sign_in_refused",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "bluesky_sign_ins_day",
                table: "settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bluesky_sign_ins_used",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_signed_in_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "site_picture_id",
                table: "post_destination",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_destination_site_picture_id",
                table: "post_destination",
                column: "site_picture_id");

            migrationBuilder.AddForeignKey(
                name: "fk_post_destination_calendar_cover_picture_site_picture_id",
                table: "post_destination",
                column: "site_picture_id",
                principalTable: "calendar_cover_picture",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_post_destination_calendar_cover_picture_site_picture_id",
                table: "post_destination");

            migrationBuilder.DropIndex(
                name: "ix_post_destination_site_picture_id",
                table: "post_destination");

            migrationBuilder.DropColumn(
                name: "bluesky_app_password_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_automated",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_did",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_display_name",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_handle",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_posting_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_server",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_session_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_sign_in_refused",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_sign_ins_day",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_sign_ins_used",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_signed_in_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_stopped_until",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "site_picture_id",
                table: "post_destination");
        }
    }
}
