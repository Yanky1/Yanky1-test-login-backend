using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class RefundLineTaxBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // data only: refund lines from before OrderShippingTax get the tax share they would have had (see RefundTaxBackfill)
            migrationBuilder.Sql(Solvelt.Api.Orders.RefundTaxBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to undo: the backfilled shares are correct values, not a schema change
        }
    }
}
