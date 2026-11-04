using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrderShippingTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ShippingTaxCents",
                table: "seller_orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ShippingTaxCents",
                table: "order_refunds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TaxCents",
                table: "order_refund_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShippingTaxCents",
                table: "seller_orders");

            migrationBuilder.DropColumn(
                name: "ShippingTaxCents",
                table: "order_refunds");

            migrationBuilder.DropColumn(
                name: "TaxCents",
                table: "order_refund_lines");
        }
    }
}
