using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class SolaPaymentProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_references_Ref",
                table: "payment_references");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId_BookingId",
                table: "payment_disputes");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId_OrderId",
                table: "payment_disputes");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "payment_references",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "stripe");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "payment_disputes",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "stripe");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "ledger_journals",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_Provider_Ref",
                table: "payment_references",
                columns: new[] { "Provider", "Ref" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_Provider_ExternalId_BookingId",
                table: "payment_disputes",
                columns: new[] { "Provider", "ExternalId", "BookingId" },
                unique: true,
                filter: "\"BookingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_Provider_ExternalId_OrderId",
                table: "payment_disputes",
                columns: new[] { "Provider", "ExternalId", "OrderId" },
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_references_Provider_Ref",
                table: "payment_references");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_Provider_ExternalId_BookingId",
                table: "payment_disputes");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_Provider_ExternalId_OrderId",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "payment_references");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "payment_disputes");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "ledger_journals");

            migrationBuilder.CreateIndex(
                name: "IX_payment_references_Ref",
                table: "payment_references",
                column: "Ref");

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_ExternalId_BookingId",
                table: "payment_disputes",
                columns: new[] { "ExternalId", "BookingId" },
                unique: true,
                filter: "\"BookingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_ExternalId_OrderId",
                table: "payment_disputes",
                columns: new[] { "ExternalId", "OrderId" },
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");
        }
    }
}
