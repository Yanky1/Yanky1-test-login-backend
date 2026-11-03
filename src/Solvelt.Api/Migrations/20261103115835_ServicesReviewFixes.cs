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
