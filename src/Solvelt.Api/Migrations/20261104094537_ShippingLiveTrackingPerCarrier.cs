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

            // existing live duplicates (the earlier check was not atomic) would make the unique index fail on PostgreSQL: stop tracking on all but the newest of each
            migrationBuilder.Sql(Solvelt.Api.Shipping.ShippingMigrationSql.StopOlderLiveDuplicates(ActiveProvider.Contains("Npgsql")));

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

            migrationBuilder.CreateIndex(
                name: "UX_order_shipments_live_tracking",
                table: "order_shipments",
                column: "TrackingNumber",
                unique: true,
                filter: "\"Method\" = 'carrier' AND \"DeliveredAt\" IS NULL AND \"TrackingStoppedAt\" IS NULL");
        }
    }
}
