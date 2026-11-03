using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class ServicesReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "bookings",
                type: "timestamp with time zone",
                nullable: true);

            // bookings that were confirmed before this column existed keep their customer data visible
            migrationBuilder.Sql("UPDATE bookings SET \"ConfirmedAt\" = \"CreatedAt\" WHERE \"Status\" IN ('Confirmed','Rescheduled','InProgress','Completed','NoShow')");

            // bookings confirmed and LATER cancelled/declined keep the date they were confirmed (from their timeline); never-confirmed ones stay null
            migrationBuilder.Sql("UPDATE bookings SET \"ConfirmedAt\" = (SELECT MIN(e.\"At\") FROM booking_events e WHERE e.\"BookingId\" = bookings.\"Id\" AND e.\"Type\" IN ('booking.confirmed','booking.approved','booking.proposal_accepted')) WHERE \"ConfirmedAt\" IS NULL AND \"Status\" IN ('CancelledByBuyer','CancelledBySeller','Declined','Expired')");

            // PRE-DEPLOY CHECK (the unique index fails on existing duplicates): SELECT "QuoteId" FROM bookings WHERE "QuoteId" IS NOT NULL GROUP BY 1 HAVING count(*) > 1;
            migrationBuilder.CreateIndex(
                name: "IX_bookings_QuoteId",
                table: "bookings",
                column: "QuoteId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bookings_QuoteId",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "bookings");
        }
    }
}
