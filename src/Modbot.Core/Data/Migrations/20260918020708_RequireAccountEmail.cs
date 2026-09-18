using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Makes an account's email address unique, and adds the switch over whether
    /// <c>GET /api/server</c> gives out the owner's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The column stays nullable. Accounts made before this exist, some with no address at all, and
    /// an account that cannot sign in because a migration demanded something it never asked for is
    /// the worst possible upgrade. They keep working and are asked for an address on their account
    /// page; everything that <em>creates</em> an account from here on requires one.
    /// </para>
    /// <para>
    /// <strong>Existing rows are made to fit rather than allowed to break the index.</strong> A
    /// unique index added over data that already violates it fails, and a migration that fails
    /// halfway leaves a deployment that will not start. So, in order, and all inside the one
    /// transaction the migration already runs in:
    /// </para>
    /// <list type="number">
    /// <item>Blank addresses become null. An empty string is not an address and would collide with
    /// every other empty string.</item>
    /// <item>Every address is trimmed and lower-cased, which is the form
    /// <c>EmailAddress.Normalize</c> stores from here on. Without this, <c>Alice@x.com</c> and
    /// <c>alice@x.com</c> stay two rows that the new rule says are one.</item>
    /// <item>Where two or more accounts then hold the same address, the <em>oldest</em> account
    /// keeps it and the others have theirs cleared. Oldest by <c>created_at</c>, ties broken by id,
    /// so the outcome does not depend on row order.</item>
    /// </list>
    /// <para>
    /// <strong>What an operator sees.</strong> Almost every deployment has one account with one
    /// address and nothing happens. Where two people had typed in the same address, the newer
    /// account loses it: that person cannot be sent a reset link and cannot sign in by email until
    /// they set their own address on their account page, and their username and password go on
    /// working the whole time. Nothing is deleted but a duplicate contact detail, and the audit log
    /// keeps every <c>modbot.user.contact.change</c> that put it there.
    /// </para>
    /// <para>
    /// Hand-edited after scaffolding: the three statements above, and the switch's default, which
    /// EF writes as the CLR default (false) where the intended default is on.
    /// </para>
    /// </remarks>
    public partial class RequireAccountEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "server_show_owner_email",
                table: "settings",
                type: "boolean",
                nullable: false,
                // On, so an upgrade does not quietly stop publishing what a deployment was
                // already willing to publish.
                defaultValue: true);

            migrationBuilder.Sql(
                """
                UPDATE modbot_user SET email = NULL WHERE btrim(coalesce(email, '')) = '';

                UPDATE modbot_user SET email = lower(btrim(email)) WHERE email IS NOT NULL;

                UPDATE modbot_user AS u SET email = NULL
                WHERE u.email IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM modbot_user AS older
                      WHERE older.email = u.email
                        AND (older.created_at, older.id) < (u.created_at, u.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_user_email",
                table: "modbot_user",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The addresses cleared on the way up are not put back: nothing recorded what they
            // were, and guessing would hand one person's address to another account.
            migrationBuilder.DropIndex(
                name: "ix_modbot_user_email",
                table: "modbot_user");

            migrationBuilder.DropColumn(
                name: "server_show_owner_email",
                table: "settings");
        }
    }
}
