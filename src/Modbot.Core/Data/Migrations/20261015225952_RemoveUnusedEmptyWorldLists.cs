using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedEmptyWorldLists : Migration
    {
        /// <summary>
        /// One-time clean-up. Before a list needed at least one world, lists could be saved empty and
        /// left behind. This deletes every list that has no worlds and that no event (of any state,
        /// deleted or not), template or shuffle points to. The Seen list (id
        /// 00000000-0000-0000-0000-000000000001, <c>WorldList.SeenId</c>) is never touched: it has no
        /// rows in <c>world_list_item</c> by design.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM world_list l
                WHERE l.id <> '00000000-0000-0000-0000-000000000001'
                  AND NOT EXISTS (SELECT 1 FROM world_list_item i WHERE i.list_id = l.id)
                  AND NOT EXISTS (SELECT 1 FROM calendar_event e WHERE e.world_list_id = l.id)
                  AND NOT EXISTS (SELECT 1 FROM calendar_template t WHERE t.world_list_id = l.id)
                  AND NOT EXISTS (SELECT 1 FROM world_list_shuffle s WHERE s.list_id = l.id);
                """);
        }

        /// <summary>
        /// Nothing to undo: the deleted lists were empty and are not kept anywhere, so they cannot be
        /// restored.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
