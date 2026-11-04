using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class PaymentsDisputeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId",
                table: "payment_disputes");

            migrationBuilder.CreateTable(
                name: "payment_unallocated_disputes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MarketplaceOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ChargeRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    AllocatedCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    EvidenceDueBy = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FlagId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_unallocated_disputes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_ExternalId",
                table: "payment_disputes",
                column: "ExternalId");

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

            migrationBuilder.CreateIndex(
                name: "IX_payment_unallocated_disputes_ExternalId",
                table: "payment_unallocated_disputes",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_unallocated_disputes_ResolvedAt",
                table: "payment_unallocated_disputes",
                column: "ResolvedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_unallocated_disputes");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId",
                table: "payment_disputes");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId_BookingId",
                table: "payment_disputes");

            migrationBuilder.DropIndex(
                name: "IX_payment_disputes_ExternalId_OrderId",
                table: "payment_disputes");

            migrationBuilder.CreateIndex(
                name: "IX_payment_disputes_ExternalId",
                table: "payment_disputes",
                column: "ExternalId",
                unique: true);
        }
    }
}
