using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupAutoInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every default below is written out rather than left to the CLR's own -- a new bool
            // is false and a new number is 0, and a settings row that predates this feature would
            // otherwise read as "nobody has to be in the instance at all" the moment somebody
            // switched it on.

            // Thirty days before the same person may be asked again. Not 0, which the column's
            // type would have given it and which means "invite them again on the next pass".
            migrationBuilder.AddColumn<int>(
                name: "group_auto_invite_again_after_days",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            // Off, deliberately, and this one agrees with the CLR default by design rather than
            // by accident: a deployment that upgraded into this switched on would start inviting
            // strangers without anybody deciding to (auto-invites design §1).
            migrationBuilder.AddColumn<bool>(
                name: "group_auto_invite_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The five-minute floor, which is also the starting value. Zero here would be the
            // floor reading as no floor at all on every row that already exists.
            migrationBuilder.AddColumn<int>(
                name: "group_auto_invite_minutes_in_instance",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            // An empty "all of": everybody who gets past the checks that are not rules. An empty
            // string here would be a column the rule reader has to guess at.
            migrationBuilder.AddColumn<string>(
                name: "group_auto_invite_rules",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"kind\":\"allOf\",\"rules\":[]}");

            migrationBuilder.CreateTable(
                name: "group_auto_invite",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    first_invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: true),
                    worked = table.Column<bool>(type: "boolean", nullable: true),
                    problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_auto_invite", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_auto_invite_invited_at",
                table: "group_auto_invite",
                column: "invited_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_auto_invite");

            migrationBuilder.DropColumn(
                name: "group_auto_invite_again_after_days",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_auto_invite_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_auto_invite_minutes_in_instance",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_auto_invite_rules",
                table: "settings");
        }
    }
}
