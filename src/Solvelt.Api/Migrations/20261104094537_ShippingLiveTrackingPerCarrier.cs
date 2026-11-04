using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class ShippingLiveTrackingPerCarrier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments");

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_TrackingNumber",
                table: "order_shipments",
                column: "TrackingNumber");

            // no data step here: the number-scoped index of the previous migration already guarantees at most one live parcel per number, so the (carrier, number) index cannot collide
            migrationBuilder.CreateIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments",
                columns: new[] { "Carrier", "TrackingNumber" },
                unique: true,
                filter: "\"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_shipments_TrackingNumber",
                table: "order_shipments");

            migrationBuilder.DropIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments");

            // FedEx and USPS parcels may now share a string: before the number-only unique index comes back, stop the older of each pair (it would fail with 23505 otherwise)
            migrationBuilder.Sql(Solvelt.Api.Shipping.ShippingMigrationSql.StopOlderLiveDuplicates);

            migrationBuilder.CreateIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments",
                column: "TrackingNumber",
                unique: true,
                filter: "\"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL");
        }
    }
}
