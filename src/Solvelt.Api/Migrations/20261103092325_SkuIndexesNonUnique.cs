using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class SkuIndexesNonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_SellerId_Sku",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_product_variants_SellerId_Sku",
                table: "product_variants");

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Sku",
                table: "products",
                columns: new[] { "SellerId", "Sku" });

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_SellerId_Sku",
                table: "product_variants",
                columns: new[] { "SellerId", "Sku" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_SellerId_Sku",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_product_variants_SellerId_Sku",
                table: "product_variants");

            migrationBuilder.CreateIndex(
                name: "IX_products_SellerId_Sku",
                table: "products",
                columns: new[] { "SellerId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_variants_SellerId_Sku",
                table: "product_variants",
                columns: new[] { "SellerId", "Sku" },
                unique: true);
        }
    }
}
