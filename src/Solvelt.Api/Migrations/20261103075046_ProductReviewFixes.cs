using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solvelt.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProductReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageSeq",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "sku_claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sku_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sku_claims_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sku_claims_ProductId",
                table: "sku_claims",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_sku_claims_SellerId_Sku",
                table: "sku_claims",
                columns: new[] { "SellerId", "Sku" },
                unique: true);
            // Backfill: existing SKUs become claims (lower-cased, trimmed). Products first, then variations; clashes (case-variants that
            // the old case-sensitive index allowed) are skipped so the migration never fails; the owner fixes them on the next edit.
            migrationBuilder.Sql(@"INSERT INTO sku_claims (""Id"", ""SellerId"", ""Sku"", ""ProductId"", ""VariantId"")
SELECT gen_random_uuid(), ""SellerId"", lower(btrim(""Sku"")), ""Id"", NULL FROM products WHERE ""Sku"" IS NOT NULL AND btrim(""Sku"") <> ''
ON CONFLICT DO NOTHING;");
            migrationBuilder.Sql(@"INSERT INTO sku_claims (""Id"", ""SellerId"", ""Sku"", ""ProductId"", ""VariantId"")
SELECT gen_random_uuid(), ""SellerId"", lower(btrim(""Sku"")), ""ProductId"", ""Id"" FROM product_variants WHERE ""Sku"" IS NOT NULL AND btrim(""Sku"") <> ''
ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sku_claims");

            migrationBuilder.DropColumn(
                name: "ImageSeq",
                table: "products");
        }
    }
}
