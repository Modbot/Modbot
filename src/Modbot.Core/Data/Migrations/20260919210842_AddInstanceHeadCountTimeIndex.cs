using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// An index only. No column is added, so nothing here turns a feature off by defaulting a new
    /// bool to false or a new number to nought.
    /// </summary>
    /// <remarks>
    /// The Instances page now reads <c>instance_head_count</c> by time — "every count taken between
    /// these two moments, whichever instance" — for its activity line and its peaks. The table only
    /// had <c>(instance_id, counted_at)</c>, which answers the per-instance question and leaves the
    /// window question a full scan of a table that grows with every change in every open instance
    /// forever.
    /// </remarks>
    public partial class AddInstanceHeadCountTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_instance_head_count_time",
                table: "instance_head_count",
                column: "counted_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_instance_head_count_time",
                table: "instance_head_count");
        }
    }
}
