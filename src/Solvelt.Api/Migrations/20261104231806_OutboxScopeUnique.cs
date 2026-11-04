using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class OutboxScopeUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages");

            // renumber first: the unique index cannot be built while two events of one scope share a number (see OutboxScopeSql)
            migrationBuilder.Sql(Solvelt.Api.Orders.OutboxScopeSql.RenumberScopes);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages",
                columns: new[] { "SequenceScope", "OrderKey" },
                unique: true,
                filter: "\"SequenceScope\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages",
                columns: new[] { "SequenceScope", "OrderKey" });
        }
    }
}
