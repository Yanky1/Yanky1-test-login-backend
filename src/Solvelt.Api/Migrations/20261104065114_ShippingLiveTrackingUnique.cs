using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class ShippingLiveTrackingUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_shipments_TrackingNumber",
                table: "order_shipments");

            // two live parcels with one number (the earlier check was not atomic) would make the unique index fail (PostgreSQL 23505): stop tracking on all but the newest of each.
            // Edited after the fact on purpose: nothing had been deployed, and this step must run BEFORE the first unique index exists.
            migrationBuilder.Sql(Solvelt.Api.Shipping.ShippingMigrationSql.StopOlderLiveDuplicates);

            migrationBuilder.CreateIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments",
                column: "TrackingNumber",
                unique: true,
                filter: "\"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments");

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_TrackingNumber",
                table: "order_shipments",
                column: "TrackingNumber");
        }
    }
}
