using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Cloud.Data.Migrations
{
    /// <summary>
    /// Cloud watching a Modbot deployment from outside: whether it has gone quiet, and who to email.
    /// </summary>
    /// <remarks>
    /// The one thing a Modbot cannot report about itself is that it is not running. Cloud can see
    /// it, because the logs stop arriving, and this row is where that watch and its state live.
    /// <c>watched</c> is named by hand: the convention would give <c>on</c>, a reserved word.
    /// </remarks>
    public partial class AddInstanceAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instance_alert",
                columns: table => new
                {
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    watched = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    silent_after_minutes = table.Column<int>(type: "integer", nullable: false),
                    errors_an_hour = table.Column<int>(type: "integer", nullable: false),
                    quiet_hours = table.Column<int>(type: "integer", nullable: false),
                    problem = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_alert", x => x.install_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_instance_alert_watched",
                table: "instance_alert",
                column: "watched");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_alert");
        }
    }
}
