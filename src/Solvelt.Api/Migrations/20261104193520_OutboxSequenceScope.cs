using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class OutboxSequenceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SequenceScope",
                table: "outbox_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages",
                columns: new[] { "SequenceScope", "OrderKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_SequenceScope_OrderKey",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "SequenceScope",
                table: "outbox_messages");
        }
    }
}
